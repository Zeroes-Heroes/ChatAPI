using System.Globalization;
using System.Security.Claims;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Database.Sequencing;
using Microsoft.EntityFrameworkCore;
using Services.Membership.Models;
using Services.Sessions;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Services.Statuses.Models;
using Services.Sync.Contracts;
using Services.Sync.Models;
using static Services.Sync.Service.SyncWire;

namespace Services.Statuses.Service;

/// <summary>
/// The v1 (sequence-space) receipt surface: ONE authenticated device reporting what it holds of ONE
/// message. Part 2 accepts <see cref="ReceiptKind.CiphertextStored"/> and
/// <see cref="ReceiptKind.Received"/> only - Seen is a one-way door that fixes a time, a reason and an
/// unread decrement, and <see cref="SyncMessageRecipientStateEntity"/> has no column for the client's
/// original observation instant yet, so accepting it here would durably record a time the later clock
/// part could not correct. It is refused explicitly rather than half-applied.
///
/// Everything below is decided from the AUTHENTICATED session and from committed server state. The
/// request's own operation identity and membership scope are only ever CHECKED against them
/// ("Server derives the device/account from authenticated session and checks supplied context").
/// </summary>
public sealed partial class MessageStatusService
{
	/// <summary>
	/// Accepts one receipt. Idempotent and monotone by construction: a timestamp that is already set is
	/// never moved, so a repeat answers <see cref="ReceiptDisposition.AlreadyApplied"/> with the same
	/// StateVersion, and nothing here touches Seen, unread or any frozen archive's public marks.
	/// </summary>
	public async Task<SyncResult<ReceiptAcceptance>> AcceptSyncReceiptAsync(
		ClaimsPrincipal caller, ReceiptRequest request, CancellationToken ct = default)
	{
		if (request is null)
		{
			return SyncResult<ReceiptAcceptance>.Fail(SyncErrorCode.InvalidRequest);
		}

		if (request.ProtocolVersion != SyncProtocol.Version)
		{
			// "Unknown protocolVersion fails before mutation" - and before the shape checks below, so a
			// future version is told what is actually wrong with it.
			return SyncResult<ReceiptAcceptance>.Fail(SyncErrorCode.UnsupportedVersion);
		}

		OperationIdentity? operation = request.Operation;
		MembershipScope? scope = request.Scope;

		if (operation?.Device is null || scope is null
			|| !RestrictedAsciiId.IsValid(operation.ClientOperationId)
			|| !RestrictedAsciiId.IsValid(operation.Device.AccountContextId)
			|| !RestrictedAsciiId.IsValid(operation.Device.DeviceRegistrationId)
			|| !RestrictedAsciiId.IsValid(operation.Device.LocalInstanceId)
			|| !RestrictedAsciiId.IsValid(scope.MembershipId)
			|| !RestrictedAsciiId.IsValid(request.MessageId)
			|| !TryParseSeq(scope.RightsVersion, out long suppliedRights)
			|| !int.TryParse(scope.ChatId, NumberStyles.None, CultureInfo.InvariantCulture, out int legacyChatId)
			|| legacyChatId <= 0 || scope.ChatId != Seq(legacyChatId)
			// A v1 MessageId is a CANONICAL LOWERCASE UUID the server minted; anything else cannot name one
			// of its rows, so it is a malformed request rather than a stale receipt. The casing is checked
			// explicitly because Guid.TryParseExact is case-insensitive: an uppercase id would otherwise
			// pass the shape check, match no row and be answered terminal NotApplicable, which would make a
			// client casing bug drop every receipt it ever sends instead of reporting itself.
			|| !IsCanonicalMessageId(request.MessageId)
			|| !Enum.IsDefined(request.Kind))
		{
			return SyncResult<ReceiptAcceptance>.Fail(SyncErrorCode.InvalidRequest);
		}

		if (request.Kind is not (ReceiptKind.CiphertextStored or ReceiptKind.Received)
			// "Only Seen carries Observation; other receipt kinds require null."
			|| request.Observation is not null)
		{
			return SyncResult<ReceiptAcceptance>.Fail(SyncErrorCode.InvalidRequest);
		}

		SyncResult<ReceiptAcceptance>? result = null;

		// The established order, unchanged: account -> device/session (TrustedMutation's own lock) -> the
		// chat's counter row -> the status rows. A receipt and a concurrent removal therefore serialize at
		// exactly one point, in either arrival order.
		TrustCheckResult trust = await TrustedMutation.RunAsync(db, trustChecker, caller, async () =>
		{
			result = await ApplyUnderLocksAsync(caller, request, operation, scope, legacyChatId, suppliedRights, ct);
		}, ct);

		if (!trust.IsTrusted)
		{
			// An infrastructure/authorization failure is NEVER a terminal answer: the device keeps the
			// receipt in its queue and reports it again after a refresh.
			return SyncResult<ReceiptAcceptance>.Fail(SyncErrorCode.SessionExpired);
		}

		// Set by every path inside the mutation above; TrustedMutation only skips the delegate when the
		// trust check already failed, which is answered above.
		return result ?? SyncResult<ReceiptAcceptance>.Fail(SyncErrorCode.Unavailable);
	}

	/// <summary>
	/// The exact form the server mints its v1 message ids in (<c>Guid.NewGuid().ToString("D")</c> in
	/// <c>MessageAcceptanceService</c>): the hyphenated 8-4-4-4-12 UUID, lowercase. A round trip through
	/// <c>Guid.ToString("D")</c> is the whole rule - "D" parsing already fixes the shape, and the ordinal
	/// comparison is what adds the case.
	/// </summary>
	private static bool IsCanonicalMessageId(string messageId) =>
		Guid.TryParseExact(messageId, "D", out Guid parsed)
		&& string.Equals(messageId, parsed.ToString("D"), StringComparison.Ordinal);

	private async Task<SyncResult<ReceiptAcceptance>> ApplyUnderLocksAsync(
		ClaimsPrincipal caller, ReceiptRequest request, OperationIdentity operation, MembershipScope scope,
		int legacyChatId, long suppliedRights, CancellationToken ct)
	{
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;

		SyncResult<ReceiptAcceptance> Answer(ReceiptDisposition disposition, long? stateVersion, bool queued = false) =>
			SyncResult<ReceiptAcceptance>.Ok(new ReceiptAcceptance(
				new ReceiptResult(
					operation,
					request.MessageId,
					disposition,
					// "Applied/AlreadyApplied have StateVersion, NotApplicable has null."
					stateVersion is long version ? Seq(version) : null,
					RfcTimestamp.Format(now)),
				queued));

		SyncCallerIdentity? identity = await SyncCallerIdentity.ResolveAsync(db, caller, ct);
		if (identity is null)
		{
			// No complete sync identity, or the session/registration/instance do not really belong
			// together. Treated transiently, exactly like an untrusted device: nothing is dropped.
			return SyncResult<ReceiptAcceptance>.Fail(SyncErrorCode.SessionExpired);
		}

		if (operation.Device != identity.Device)
		{
			// The whole tuple or nothing: a receipt naming another account, registration or SQLite copy is
			// refused rather than silently re-attributed to this session's own device.
			return SyncResult<ReceiptAcceptance>.Fail(SyncErrorCode.InvalidRequest);
		}

		// THE serialization point with every rights change, and the same row the chat's sequence allocator
		// takes - so the journal entries below cost no contention this transaction did not already have.
		// It also applies the fresh-start boundary: a superseded context's chat answers Denied.
		ChatAccess access = await accessAuthority.LockChatAndAuthorizeAsync(caller, legacyChatId, ct);
		if (!access.CanRead)
		{
			// Terminal, and indistinguishable from a chat that does not exist: a stale receipt must end
			// without discovering whether somebody else owns these identifiers.
			return Answer(ReceiptDisposition.NotApplicable, null);
		}

		// The NAMED stint, owned by this caller's own account. A frozen one is legitimate here - an archive
		// is downloaded and confirmed exactly like current history - so its state is read, not required.
		SyncMembershipEntity? membership = await db.SyncMemberships.AsNoTracking().SingleOrDefaultAsync(
			m => m.Id == scope.MembershipId
				&& m.ChatId == scope.ChatId
				&& m.AccountId == identity.AccountContextId, ct);
		if (membership is null)
		{
			return Answer(ReceiptDisposition.NotApplicable, null);
		}

		if (suppliedRights > membership.RightsVersion)
		{
			// The client claims rights this server never issued: not a stale receipt but a diverged view,
			// so it reconciles rather than dropping anything. A LOWER (stale) version is accepted on
			// purpose - an offline receipt carries the rights version of its enqueue moment, and the
			// authority for whether it still applies is the grant check below, not that number.
			return SyncResult<ReceiptAcceptance>.Fail(SyncErrorCode.RightsChanged);
		}

		SyncMessageEntity? message = await db.SyncMessages.AsNoTracking().SingleOrDefaultAsync(
			m => m.Id == request.MessageId && m.ChatId == scope.ChatId, ct);
		if (message is null)
		{
			return Answer(ReceiptDisposition.NotApplicable, null);
		}

		// The NAMED stint's own sequence grants - not the union of this person's windows, and not
		// ChatAccess.WindowAt, which prefers a still-open window over a frozen one covering the same
		// instant. After a rejoin with an overlapping period the two stints mean different things about the
		// same message, and the device said which one it is reporting under.
		bool permitted = await db.SyncMembershipHistoryGrants.AsNoTracking().AnyAsync(
			g => g.MembershipId == membership.Id
				&& g.FromMessageSequence <= message.MessageSequence
				&& (g.ThroughMessageSequence == null || g.ThroughMessageSequence >= message.MessageSequence), ct);
		if (!permitted)
		{
			return Answer(ReceiptDisposition.NotApplicable, null);
		}

		// ---- private device progress: per registration AND local instance, nobody else's business.

		SyncMessageDeviceStateEntity? state = await db.SyncMessageDeviceStates.SingleOrDefaultAsync(
			s => s.MessageId == message.Id
				&& s.DeviceRegistrationId == identity.DeviceRegistrationId
				&& s.LocalInstanceId == identity.LocalInstanceId, ct);

		if (state is null)
		{
			state = new SyncMessageDeviceStateEntity(
				message.Id, message.ChatId, identity.DeviceRegistrationId, identity.LocalInstanceId,
				identity.AccountContextId, membership.Id, version: 0);
			db.SyncMessageDeviceStates.Add(state);
		}

		// The row is keyed by (message, registration, instance) and deliberately NOT by the stint: the
		// FIRST receipt fixes which stint this copy was obtained under, and a later receipt naming the
		// rejoined stint must not silently move the private archive progress of the old one.
		bool deviceChanged = false;
		if (state.CiphertextStoredAtUtc is null)
		{
			// A readable copy implies the bytes are on disk, so Received back-fills the earlier step rather
			// than leaving a device that only reported Received looking like it holds nothing.
			state.CiphertextStoredAtUtc = now;
			deviceChanged = true;
		}

		if (request.Kind == ReceiptKind.Received && state.ReceivedAtUtc is null)
		{
			state.ReceivedAtUtc = now;
			deviceChanged = true;
		}

		bool queued = false;
		if (deviceChanged)
		{
			state.Version++;
			// The reference is this SQLite copy alone - one id, which is what a pointer at the row that
			// changed needs, and never the registration and the instance concatenated (a pair is what the
			// column could not hold). Delivery derives the registration from the row itself.
			queued |= await AppendStatusChangeAsync(message, SyncChangeKind.DeviceState, identity.LocalInstanceId, now, ct);
		}

		// ---- the person's public mark. The rows are per STINT - a rejoined person has more than one, and
		// the OwnSend pass writes its own current-stint row independently of the guard below - so the ONE
		// fact a reader shows is the fold in Services.Statuses.Models.PublicRecipientState, never a row
		// picked on its own.

		bool publicChanged = false;
		if (request.Kind == ReceiptKind.Received
			// A FROZEN stint stops here: "Публичните отметки и unread в замразения чат остават
			// непроменени" - only the private row above moves for an archive.
			&& membership.State == SyncMembershipState.Active
			// An author is not its own recipient: it is excluded from every proportion
			// ("Получателите се броят по хора, като подателят е изключен").
			&& !string.Equals(message.SenderAccountId, identity.AccountContextId, StringComparison.Ordinal))
		{
			List<SyncMessageRecipientStateEntity> existing = await db.SyncMessageRecipientStates
				.Where(s => s.MessageId == message.Id && s.AccountId == identity.AccountContextId)
				.ToListAsync(ct);

			// "На автора „Изпратено" за даден получател се показва след Received от поне едно негово
			// устройство" - the first device that proves it owns the moment, whichever stint it reported
			// under, so a rejoin never reports the same person as received twice.
			if (!existing.Any(s => s.ReceivedAtUtc is not null))
			{
				// The NAMED stint's own row if it already has one - an OwnSend mark could have created it
				// for this very stint - and a new one otherwise. Never another stint's row: an archive's
				// public mark is immutable, and the reader folds the two anyway.
				SyncMessageRecipientStateEntity? recipient = existing.FirstOrDefault(s => s.MembershipId == membership.Id);
				if (recipient is null)
				{
					recipient = new SyncMessageRecipientStateEntity(
						message.Id, message.ChatId, membership.Id, identity.AccountContextId, version: 0);
					db.SyncMessageRecipientStates.Add(recipient);
				}

				recipient.ReceivedAtUtc = now;
				recipient.Version++;
				publicChanged = true;

				queued |= await AppendStatusChangeAsync(message, SyncChangeKind.RecipientState, identity.AccountContextId, now, ct);
			}
		}

		// Nothing here touches SeenAtUtc, SeenReason or any unread counter: a Received is not an
		// observation, and recomputing unread is Seen-only by design.
		await db.SaveChangesAsync(ct);

		return Answer(
			deviceChanged || publicChanged ? ReceiptDisposition.Applied : ReceiptDisposition.AlreadyApplied,
			// The version of the row this device actually owns - monotone per (message, registration,
			// instance), which is what makes a retried receipt answer the same number.
			state.Version,
			queued);
	}

	/// <summary>
	/// One journal entry for a committed status change, plus - when status delivery is enabled - its
	/// outbox row. The outbox row carries its OWN kind and reference: the journal is a liveness/cursor
	/// mechanism retention may prune, and a task that waited longer than that must still know what it is
	/// delivering and to whom.
	/// </summary>
	/// <returns>Whether an outbox row was queued, so the caller can wake this replica's publisher.</returns>
	private async Task<bool> AppendStatusChangeAsync(
		SyncMessageEntity message, SyncChangeKind kind, string secondaryId, DateTime now, CancellationToken ct)
	{
		long sequence = await SyncSequenceAllocator.AllocateChangeSequenceAsync(db, message.ChatId, ct);

		db.SyncChatChanges.Add(new SyncChatChangeEntity(message.ChatId, sequence, kind, message.Id, now)
		{
			SecondaryId = secondaryId,
		});

		if (!delivery.Value.PublishStatusChanges)
		{
			// Default-off deployment gate: an outbox row whose kind an older consumer does not read would
			// be delivered as a full message. Until every consumer is upgraded the change is still durable
			// and still reaches every device through the HTTP delta, which reads the journal entry above.
			return false;
		}

		db.SyncOutbox.Add(new SyncOutboxEntity(message.ChatId, message.Id, sequence, now)
		{
			ChangeKind = kind,
			SecondaryId = secondaryId,
		});

		return true;
	}
}
