using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Database.Sequencing;
using Microsoft.EntityFrameworkCore;
using Services.Authorship.Interface;
using Services.Crypto;
using Services.Crypto.Interface;
using Services.Crypto.Models;
using Services.Keys.Interface;
using Services.Membership;
using Services.Membership.Interface;
using Services.Membership.Models;
using Services.Messaging.Interface;
using Services.Messaging.Models;
using Services.Sessions;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Services.Statuses.Interface;
using Services.Statuses.Models;
using Services.Sync.Contracts;
using Services.Utilities;

namespace Services.Messaging.Service;

/// <summary>See <see cref="IMessageAcceptanceService"/>.</summary>
public sealed class MessageAcceptanceService(
	AppDbContext db,
	IMessagePackageVerifier verifier,
	IAuthorKeyRegistry authorKeys,
	IChatAccessAuthority accessAuthority,
	IChatMembershipService membership,
	IMessageKeyProvisioningService messageKeys,
	IProtectedMessageKeyStore keyStore,
	IMessageStatusService statuses,
	IAccountContextBoundary boundary,
	ISessionTrustChecker trustChecker,
	TimeProvider timeProvider) : IMessageAcceptanceService
{
	/// <summary>A transient refusal says how long to wait; a definitive one never does.</summary>
	private const int SessionRetryAfterMs = 1000;

	private const int MessageKeyBytes = 32;

	public async Task<Result<AcceptanceOutcome>> AcceptAsync(
		ClaimsPrincipal caller, SendMessageRequest request, CancellationToken ct = default)
	{
		if (request?.SignedPackage is null || request.Scope?.ChatId is null || request.MessageKey is null)
		{
			return Result<AcceptanceOutcome>.Failure("Malformed send request.", HttpStatusCode.BadRequest);
		}

		if (await DeviceOfAsync(caller, ct) is not DeviceContext device)
		{
			return Result<AcceptanceOutcome>.Failure("This session has no local instance.", HttpStatusCode.BadRequest);
		}

		// The package names the author key VERSION a signature must be checked with, and nothing else
		// can. Read without trusting: every value used for a decision below comes either from the
		// session claims or from state the server already authorized, and the package is then verified
		// against that - see IMessagePackageVerifier.ReadClaimedBinding.
		MessageBinding claimed;
		try
		{
			claimed = verifier.ReadClaimedBinding(request.SignedPackage);
		}
		catch (Exception ex) when (ex is JwsFormatException or MessageBindingMismatchException)
		{
			// No operation can be named at all - the client's own saved package is unusable. Answered as
			// a transport failure rather than as some operation's outcome.
			return Result<AcceptanceOutcome>.Failure("Signed package is malformed.", HttpStatusCode.BadRequest);
		}

		// The identity the answer is about: the DEVICE half is the session's, never the package's. A
		// package claiming another device is refused below rather than silently re-attributed.
		OperationIdentity identity = new(device, claimed.Operation.ClientOperationId);

		if (request.ProtocolVersion != SyncProtocol.Version)
		{
			// "Unknown protocolVersion fails before mutation."
			return Terminal(identity, OperationErrorCode.UnsupportedVersion);
		}

		if (!claimed.Operation.Device.Equals(device)
			|| !string.Equals(claimed.AuthorKey.AccountContextId, device.AccountContextId, StringComparison.Ordinal))
		{
			return Terminal(identity, OperationErrorCode.Forbidden);
		}

		if (!string.Equals(claimed.ChatId, request.Scope.ChatId, StringComparison.Ordinal))
		{
			// The scope is the mutable half of the request and the binding the immutable one; they must
			// name the same chat or neither can be trusted to describe this send.
			return Terminal(identity, OperationErrorCode.InvalidPackage);
		}

		if (Encoding.UTF8.GetByteCount(request.SignedPackage) > SyncProtocol.MaxSignedPackageBytes)
		{
			// "Oversize requests fail PayloadTooLarge before acceptance."
			return Terminal(identity, OperationErrorCode.PayloadTooLarge);
		}

		if (!Base64Url.TryDecode(request.MessageKey, out byte[] messageKey) || messageKey.Length != MessageKeyBytes)
		{
			return Terminal(identity, OperationErrorCode.InvalidPackage);
		}

		if (!int.TryParse(claimed.ChatId, NumberStyles.None, CultureInfo.InvariantCulture, out int legacyChatId))
		{
			return Terminal(identity, OperationErrorCode.Forbidden);
		}

		// Fresh start (plan/06): a new account context may never publish into the old context's chats.
		if (await boundary.IsLegacyChatExcludedAsync(caller, legacyChatId, ct))
		{
			return Terminal(identity, OperationErrorCode.Forbidden);
		}

		try
		{
			return await AcceptUnderLocksAsync(caller, request, identity, claimed, legacyChatId, messageKey, ct);
		}
		finally
		{
			// "SendMessageRequest.MessageKey ... never persist them directly" - this copy existed only to
			// be wrapped by the key module, and leaves no plaintext behind in this scope.
			CryptographicOperations.ZeroMemory(messageKey);
		}
	}

	public async Task<Result<AcceptanceOutcome>> QueryAsync(
		ClaimsPrincipal caller, OperationQuery query, CancellationToken ct = default)
	{
		if (query?.Operation?.Device is null || !RestrictedAsciiId.IsValid(query.Operation.ClientOperationId))
		{
			return Result<AcceptanceOutcome>.Failure("Malformed operation query.", HttpStatusCode.BadRequest);
		}

		if (await DeviceOfAsync(caller, ct) is not DeviceContext device)
		{
			return Result<AcceptanceOutcome>.Failure("This session has no local instance.", HttpStatusCode.BadRequest);
		}

		OperationIdentity identity = new(device, query.Operation.ClientOperationId);

		if (query.ProtocolVersion != SyncProtocol.Version)
		{
			return Terminal(identity, OperationErrorCode.UnsupportedVersion);
		}

		if (!query.Operation.Device.Equals(device))
		{
			// An operation of another device is not "unknown" to this caller - it is simply none of its
			// business, and must not be probeable.
			return Terminal(identity, OperationErrorCode.Forbidden);
		}

		// No membership check anywhere on this path, by contract: an own accepted operation is recognised
		// after removal, blocking or a frozen archive.
		SyncOperationResultEntity? stored = await FindResultAsync(identity, ct);
		if (stored is null)
		{
			return Result<AcceptanceOutcome>.Success(AcceptanceOutcome.Of(new UnknownOperation(identity)));
		}

		return Result<AcceptanceOutcome>.Success(AcceptanceOutcome.Of(await DescribeStoredAsync(identity, stored, ct)));
	}

	/// <summary>
	/// Everything from here on runs inside ONE transaction: the device/session lock, the chat counter row,
	/// the operation-identity advisory lock, the result lookup, the verification and the writes. A crash
	/// anywhere before the commit leaves nothing at all - the client still holds its saved package and its
	/// operation id, and retries into the very same decision.
	/// </summary>
	private async Task<Result<AcceptanceOutcome>> AcceptUnderLocksAsync(
		ClaimsPrincipal caller, SendMessageRequest request, OperationIdentity identity, MessageBinding claimed,
		int legacyChatId, byte[] messageKey, CancellationToken ct)
	{
		string chatId = claimed.ChatId;
		string digest = verifier.ComputeDigest(request.SignedPackage);
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;

		OperationResult? answer = null;
		IReadOnlyList<RecipientStatusMark> ownSendSeen = [];
		long? outboxId = null;

		TrustCheckResult trust = await TrustedMutation.RunAsync(db, trustChecker, caller, async () =>
		{
			// The message, its key, its journal entries and its outbox row all point at SyncChats /
			// SyncChatSequenceCounters, so a pre-stage-07 chat is projected first - the membership
			// module's own lazy projection, under its own advisory lock.
			try
			{
				await membership.EnsureProjectedAsync(legacyChatId, ct);
			}
			catch (ChatMembershipRefusedException)
			{
				answer = new RejectedOperation(identity, OperationErrorCode.Forbidden);
				return;
			}

			// THE serialization point with every rights change: the chat's own counter row. A concurrent
			// removal/leave/block either commits before this read (and this send is then refused) or waits
			// behind this transaction.
			ChatAccess access = await accessAuthority.LockChatAndAuthorizeAsync(caller, legacyChatId, ct);

			// Taken last, and by nothing else in the codebase, so it can close no cycle: it serializes two
			// concurrent attempts at the same operation identity even when they name different chats (and
			// therefore lock different counter rows).
			await SyncChatIdentity.LockKeyAsync(db, OperationLockKey(identity), ct);

			// Consulted BEFORE any rights check: an own accepted operation is recognised after a
			// membership change ("Удостоверена собствена приета операция се разпознава и след промяна на
			// членството"), and a concurrent retry that lost the race for these locks observes the
			// committed result here instead of accepting a second time.
			if (await FindResultAsync(identity, ct) is SyncOperationResultEntity stored)
			{
				answer = string.Equals(stored.PackageDigest, digest, StringComparison.Ordinal)
					? await DescribeStoredAsync(identity, stored, ct)
					// "различен пакет със същата идентичност се отхвърля" - and the stored result is
					// neither deleted nor replaced.
					: new RejectedOperation(identity, OperationErrorCode.OperationConflict);
				return;
			}

			if (!access.CanRead)
			{
				answer = new RejectedOperation(identity, OperationErrorCode.Forbidden);
				return;
			}

			if (!access.CanSendNewMessages)
			{
				// A frozen archive is read-only and a suspended personal chat accepts no new
				// correspondence. Deliberately NOT persisted as a terminal result: rights can legitimately
				// come back, and a stored row would then answer for a state that no longer exists.
				answer = new RejectedOperation(identity, access.PersonalCorrespondenceSuspended
					? OperationErrorCode.Forbidden
					: OperationErrorCode.MembershipChanged);
				return;
			}

			SyncMembershipEntity? stint = await db.SyncMemberships.FirstOrDefaultAsync(
				m => m.ChatId == chatId && m.AccountId == identity.Device.AccountContextId && m.State == SyncMembershipState.Active, ct);
			if (stint is null)
			{
				answer = new RejectedOperation(identity, OperationErrorCode.Forbidden);
				return;
			}

			// "Server derives the device/account from authenticated session and checks supplied context":
			// the scope is checked against the authoritative stint, never used in place of it.
			if (!string.Equals(request.Scope.MembershipId, stint.Id, StringComparison.Ordinal)
				|| !string.Equals(request.Scope.RightsVersion, stint.RightsVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
			{
				answer = new RejectedOperation(identity, OperationErrorCode.MembershipChanged);
				return;
			}

			byte[]? spki = await authorKeys.FindSpkiAsync(claimed.AuthorKey, ct);
			if (spki is null)
			{
				// Nothing is bound to this reference, so there is no key to verify against - and the
				// package's own may never stand in for one. Not persisted: the device can register the
				// author context and retry the SAME package.
				answer = new RejectedOperation(identity, OperationErrorCode.InvalidPackage);
				return;
			}

			// The expected binding is built entirely from authorized state: the session's device, the chat
			// this transaction has locked and authorized, and the registry reference the SPKI came from.
			MessageBinding expected = new(SyncProtocol.Version, identity, chatId, claimed.AuthorKey, OperationKind.SendText);

			VerifiedMessagePackage verified;
			try
			{
				verified = verifier.Verify(request.SignedPackage, expected, spki);
			}
			catch (Exception ex) when (ex is JwsFormatException or JwsSignatureException or MessageBindingMismatchException)
			{
				// A package that does not verify against the key it names can never verify, so this one IS
				// persisted: a retry of the same bytes answers identically without re-doing the work.
				PersistResult(identity, digest, SyncOperationOutcome.Rejected, chatId, null, SyncOperationErrorCode.InvalidPackage, now);
				await db.SaveChangesAsync(ct);
				answer = new RejectedOperation(identity, OperationErrorCode.InvalidPackage);
				return;
			}

			(long messageSequence, long changeSequence) = await SyncSequenceAllocator.AllocateMessageAndChangeSequenceAsync(db, chatId, ct);

			// MessageId is the server's, a lowercase UUID, and is fixed for this operation forever: a
			// retry re-reads it from the stored result instead of allocating a second one.
			SyncMessageEntity message = new(
				Guid.NewGuid().ToString("D"), chatId, identity.Device.AccountContextId, stint.Id,
				messageSequence, changeSequence, SyncOperationKind.SendText,
				claimed.AuthorKey.AuthorKeyId, claimed.AuthorKey.AuthorKeyVersion,
				request.SignedPackage, verified.PackageDigest, now);
			db.SyncMessages.Add(message);

			// The protected key row, in this very transaction - the key module never opens one of its own.
			keyStore.Stage(db, messageKeys.ProtectForStorage(message.Id, chatId, messageKey, now));

			// The message's own journal entry sits at the change sequence allocated with it, so the chat's
			// change stream and its message stream agree about this message's position.
			db.SyncChatChanges.Add(new SyncChatChangeEntity(chatId, changeSequence, SyncChangeKind.Message, message.Id, now));

			PersistResult(identity, digest, SyncOperationOutcome.Accepted, chatId, message.Id, null, now);

			SyncOutboxEntity outbox = new(chatId, message.Id, changeSequence, now);
			db.SyncOutbox.Add(outbox);

			// The message row has to exist before the status rows' foreign keys and before the status
			// service's own queries can see it.
			await db.SaveChangesAsync(ct);

			NewMessageStatusEffects effects = await statuses.RecordNewSyncMessageAsync(caller, access, legacyChatId, message, ct);
			ownSendSeen = effects.OwnSendSeen;

			foreach (string membershipId in effects.UnreadMembershipIds)
			{
				await AppendChangeAsync(chatId, SyncChangeKind.Unread, membershipId, null, now, ct);
			}

			foreach (string seenMessageId in effects.OwnSendSyncMessageIds)
			{
				await AppendChangeAsync(chatId, SyncChangeKind.RecipientState, seenMessageId, identity.Device.AccountContextId, now, ct);
			}

			await db.SaveChangesAsync(ct);

			outboxId = outbox.Id;
			answer = new AcceptedOperation(identity, Describe(identity, message, messageSequence));
		}, ct);

		if (!trust.IsTrusted)
		{
			// Transient by contract, never Rejected: the device may well be trusted again after a refresh,
			// and the saved package must survive.
			return Result<AcceptanceOutcome>.Success(AcceptanceOutcome.Untrusted(
				new RetryableOperation(identity, OperationErrorCode.SessionExpired, SessionRetryAfterMs)));
		}

		return Result<AcceptanceOutcome>.Success(new AcceptanceOutcome(answer!, ownSendSeen, outboxId, SessionUntrusted: false));
	}

	/// <summary>
	/// The committed result as an <see cref="OperationResult"/>. An Accepted row is described from the
	/// MESSAGE it points at, so a retry gets the original MessageId, creation instant and sequences -
	/// including <c>OwnSendThroughSequence</c>, which IS that message's own MessageSequence and therefore
	/// cannot move ("OwnSend запазва първоначалната граница при повторения").
	/// </summary>
	private async Task<OperationResult> DescribeStoredAsync(
		OperationIdentity identity, SyncOperationResultEntity stored, CancellationToken ct)
	{
		if (stored.Outcome == SyncOperationOutcome.Rejected)
		{
			return new RejectedOperation(identity, Map(stored.ErrorCode));
		}

		SyncMessageEntity? message = stored.MessageId is null
			? null
			: await db.SyncMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == stored.MessageId, ct);

		return message is null
			// An Accepted row without its message cannot happen - they commit together and neither is
			// deletable - so this is a fail-closed answer, never a silent re-acceptance.
			? new RetryableOperation(identity, OperationErrorCode.Unavailable, SessionRetryAfterMs)
			: new AcceptedOperation(identity, Describe(identity, message, message.MessageSequence));
	}

	private static AcceptedMessage Describe(OperationIdentity identity, SyncMessageEntity message, long ownSendThroughSequence) =>
		new(identity,
			message.PackageDigest,
			message.Id,
			message.ChatId,
			Sequence(message.MessageSequence),
			Sequence(message.ChangeSequence),
			RfcTimestamp.Format(message.CreatedAtUtc),
			Sequence(ownSendThroughSequence));

	private void PersistResult(
		OperationIdentity identity, string digest, SyncOperationOutcome outcome, string chatId, string? messageId,
		SyncOperationErrorCode? errorCode, DateTime now) =>
		db.SyncOperationResults.Add(new SyncOperationResultEntity(
			identity.Device.AccountContextId, identity.Device.DeviceRegistrationId, identity.Device.LocalInstanceId,
			identity.ClientOperationId, digest, outcome, now)
		{
			ChatId = chatId,
			MessageId = messageId,
			ErrorCode = errorCode,
		});

	private Task<SyncOperationResultEntity?> FindResultAsync(OperationIdentity identity, CancellationToken ct) =>
		db.SyncOperationResults.AsNoTracking().FirstOrDefaultAsync(
			r => r.AccountId == identity.Device.AccountContextId
				&& r.DeviceRegistrationId == identity.Device.DeviceRegistrationId
				&& r.LocalInstanceId == identity.Device.LocalInstanceId
				&& r.ClientOperationId == identity.ClientOperationId, ct);

	private async Task AppendChangeAsync(
		string chatId, SyncChangeKind kind, string referenceId, string? secondaryId, DateTime now, CancellationToken ct)
	{
		long sequence = await SyncSequenceAllocator.AllocateChangeSequenceAsync(db, chatId, ct);
		db.SyncChatChanges.Add(new SyncChatChangeEntity(chatId, sequence, kind, referenceId, now)
		{
			SecondaryId = secondaryId,
		});
	}

	/// <summary>The caller's own device, as the server knows it - never taken from the request body.</summary>
	private async Task<DeviceContext?> DeviceOfAsync(ClaimsPrincipal caller, CancellationToken ct)
	{
		string? accountId = caller.FindFirstValue(SessionClaimTypes.AccountContextId);
		string? deviceId = caller.FindFirstValue(SessionClaimTypes.DeviceRegistrationId);
		string? instanceId = caller.FindFirstValue(SessionClaimTypes.LocalInstanceId);

		if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(instanceId))
		{
			return null;
		}

		// The claims are server-issued, but these are the rows the operation result's foreign keys will
		// point at: this is what keeps an operation from being recorded against another account's device.
		bool owned = await db.SyncLocalInstances.AsNoTracking()
			.AnyAsync(i => i.Id == instanceId && i.DeviceRegistrationId == deviceId && i.DeviceRegistration.AccountId == accountId, ct);

		return owned ? new DeviceContext(accountId, deviceId, instanceId) : null;
	}

	/// <summary>Advisory-lock key for one exact operation identity - the whole tuple, so two devices' identical clientOperationIds never contend.</summary>
	private static string OperationLockKey(OperationIdentity identity) =>
		$"operation:{identity.Device.AccountContextId}:{identity.Device.DeviceRegistrationId}:{identity.Device.LocalInstanceId}:{identity.ClientOperationId}";

	private static Result<AcceptanceOutcome> Terminal(OperationIdentity identity, OperationErrorCode code) =>
		Result<AcceptanceOutcome>.Success(AcceptanceOutcome.Of(new RejectedOperation(identity, code)));

	private static string Sequence(long value) => value.ToString(CultureInfo.InvariantCulture);

	private static OperationErrorCode Map(SyncOperationErrorCode? code) => code switch
	{
		SyncOperationErrorCode.Unavailable => OperationErrorCode.Unavailable,
		SyncOperationErrorCode.RateLimited => OperationErrorCode.RateLimited,
		SyncOperationErrorCode.SessionExpired => OperationErrorCode.SessionExpired,
		SyncOperationErrorCode.DeviceRevoked => OperationErrorCode.DeviceRevoked,
		SyncOperationErrorCode.MembershipChanged => OperationErrorCode.MembershipChanged,
		SyncOperationErrorCode.Forbidden => OperationErrorCode.Forbidden,
		SyncOperationErrorCode.OperationConflict => OperationErrorCode.OperationConflict,
		SyncOperationErrorCode.UnsupportedVersion => OperationErrorCode.UnsupportedVersion,
		SyncOperationErrorCode.PayloadTooLarge => OperationErrorCode.PayloadTooLarge,
		_ => OperationErrorCode.InvalidPackage,
	};
}
