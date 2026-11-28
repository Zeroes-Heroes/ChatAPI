using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Services.Authorship.Interface;
using Services.Crypto;
using Services.Delivery.Interface;
using Services.Delivery.Models;
using Services.Hubs;
using Services.Keys.Interface;
using Services.Keys.Models;
using Services.Membership.Interface;
using Services.Membership.Models;
using Services.Sessions;
using Services.Sync.Contracts;
using Services.Sync.Models;
using Services.Sync.Service;
using Services.Utilities.Statics;

namespace Services.Delivery.Service;

/// <summary>See <see cref="ILiveDeliveryService"/>.</summary>
public sealed class LiveDeliveryService(
	AppDbContext db,
	IChatAccessAuthority authority,
	IAuthorKeyRegistry authorKeys,
	IMessageKeyProvisioningService messageKeys,
	IProtectedMessageKeyStore keyStore,
	SyncProjection projection,
	IHubContext<BaseHub> hub,
	ILogger<LiveDeliveryService> logger) : ILiveDeliveryService
{
	/// <summary>The v1 encoding, built once - the same JSON the hub's own v1 methods speak, never the legacy MessagePack resolver.</summary>
	private static readonly JsonSerializerOptions SyncJson = SyncProtocol.CreateJsonOptions();

	/// <summary>One recipient's reauthorized scope for this exact change, as the live event has to name it.</summary>
	private sealed record Audience(string AccountContextId, SyncScopeContext Scope);

	public async Task<DeliveryOutcome> DeliverAsync(DeliveryTask task, CancellationToken ct = default)
	{
		// WHAT this task delivers is the durable outbox row's own business, never the broker payload's and
		// never the chat journal's: the journal is a liveness/cursor mechanism that retention prunes after
		// 30 days, and a task that waited that long must still know its kind and its subject. The broker
		// format is unchanged - the row is re-read here by the id the task already carried.
		SyncOutboxEntity? row = await db.SyncOutbox.AsNoTracking().FirstOrDefaultAsync(o => o.Id == task.OutboxId, ct);
		if (row is null)
		{
			// The row and its change commit together and neither is deletable, so this is a broken record
			// rather than a race.
			return DeliveryOutcome.Unprocessable("No outbox row for this delivery task.");
		}

		if (!string.Equals(row.ChatId, task.ChatId, StringComparison.Ordinal)
			|| !string.Equals(row.MessageId, task.MessageId, StringComparison.Ordinal)
			|| !string.Equals(Sequence(row.ChangeSequence), task.ChangeSequence, StringComparison.Ordinal))
		{
			// A task that does not describe its own row cannot be trusted to name the change either; it is
			// recorded with its reason instead of being delivered against the wrong row.
			return DeliveryOutcome.Unprocessable("Delivery task does not match its outbox row.");
		}

		SyncMessageEntity? message = await db.SyncMessages.AsNoTracking()
			.FirstOrDefaultAsync(m => m.Id == row.MessageId && m.ChatId == row.ChatId, ct);
		if (message is null)
		{
			return DeliveryOutcome.Unprocessable("No message row for this outbox task.");
		}

		if (!int.TryParse(message.ChatId, NumberStyles.None, CultureInfo.InvariantCulture, out int legacyChatId))
		{
			return DeliveryOutcome.Unprocessable("Chat id does not name a legacy chat.");
		}

		return row.ChangeKind switch
		{
			// Private per-device progress: delivered to the exact registration AND local instance that
			// reported it, and to nothing else - and to nothing at all when the stint it was reported under
			// is frozen, whose stream ends at its own bound. No content key is touched at all.
			SyncChangeKind.DeviceState => await DeliverDeviceStateAsync(row, message, ct),

			// The person's public mark: delivered to everyone whose rights to THIS message are current
			// right now, with every frozen archive left out.
			SyncChangeKind.RecipientState => await DeliverRecipientStateAsync(row, message, legacyChatId, ct),

			SyncChangeKind.Message => await DeliverMessageAsync(task, message, legacyChatId, ct),

			// A kind no producer queues yet. Kept with its reason rather than guessed at - guessing would
			// mean shipping the message body for a change that is not a message.
			_ => DeliveryOutcome.Unprocessable($"Outbox kind {row.ChangeKind} has no live delivery."),
		};
	}

	// ---------------------------------------------------------------- the message itself

	private async Task<DeliveryOutcome> DeliverMessageAsync(
		DeliveryTask task, SyncMessageEntity message, int legacyChatId, CancellationToken ct)
	{
		SyncMessageKeyEntity? keyRow = await keyStore.FindAsync(db, message.Id, ct);
		if (keyRow is null)
		{
			// Without the content key no recipient could ever read this message; delivering the package
			// alone would produce a permanently undecryptable record on every device.
			return DeliveryOutcome.Unprocessable("No protected key row for this message.");
		}

		// Every delivery carries the author's verification key, and it comes from the server-authorized
		// registry keyed by the complete reference - never from the package's own header.
		AuthorKeyReference reference = new(
			message.SenderAccountId, message.ChatId, message.AuthorKeyId, message.AuthorKeyVersion);
		byte[]? spki = await authorKeys.FindSpkiAsync(reference, ct);
		if (spki is null)
		{
			return DeliveryOutcome.Unprocessable("The author key reference of this message is not bound to a key.");
		}

		IReadOnlyList<Audience> audience = await AuthorizedAudienceAsync(
			message, legacyChatId, task.ChangeSequence, includeFrozen: true, ct);
		if (audience.Count == 0)
		{
			return DeliveryOutcome.Delivered(0);
		}

		ILookup<string, string> devicesOf = await TrustedDevicesAsync([.. audience.Select(a => a.AccountContextId)], ct);

		// "Live delivery materializes keys only after final recipient authorization" - the key is unwrapped
		// here, once the recipient set is known, and never leaves this scope in the clear.
		byte[] messageKey = messageKeys.UnwrapForAuthorizedDelivery(
			keyRow, new MessageKeyAccessContext(message.Id, message.ChatId));
		try
		{
			MessageEnvelope envelope = new(
				message.Id,
				message.ChatId,
				message.SenderAccountId,
				Sequence(message.MessageSequence),
				// A message is immutable, so its version is simply its own position in the chat's change
				// stream: a later page can never "replace" it with something older.
				Sequence(message.ChangeSequence),
				RfcTimestamp.Format(message.CreatedAtUtc),
				message.SignedPackage);
			MessageKeyGrant grant = new(message.Id, keyRow.KeyVersion, Base64Url.Encode(messageKey));
			AuthorVerificationKey verificationKey = new(reference, Base64Url.Encode(spki));

			int sends = 0;
			foreach (Audience recipient in audience)
			{
				MembershipScope scope = recipient.Scope.Scope;
				string payload = WriteEvent(
					recipient,
					new SyncChange(
						task.EventId,
						new StreamPosition(StreamKind.Chat, message.ChatId, recipient.Scope.MembershipId, task.ChangeSequence),
						scope,
						ChangeKind.Message,
						Chat: null,
						Message: new MessageDelivery(envelope, grant, verificationKey, scope),
						RecipientState: null,
						DeviceState: null,
						Unread: null,
						Membership: null,
						MembershipEvent: null));

				sends += await SendToDevicesAsync(devicesOf[recipient.AccountContextId], payload, ct);
			}

			return DeliveryOutcome.Delivered(sends);
		}
		finally
		{
			// The transient key existed only to be granted; nothing in this scope keeps it afterwards.
			CryptographicOperations.ZeroMemory(messageKey);
		}
	}

	// ---------------------------------------------------------------- statuses

	/// <summary>
	/// The public per-person mark. Its audience is re-derived from CURRENT rights, exactly like a
	/// message's, and then narrowed: a frozen archive is resolved at its own snapshot and must not receive
	/// anything that happened after it was frozen ("Последващи съобщения, статуси ... остават извън
	/// достъпа му"), so only still-open stints are addressed.
	/// </summary>
	private async Task<DeliveryOutcome> DeliverRecipientStateAsync(
		SyncOutboxEntity row, SyncMessageEntity message, int legacyChatId, CancellationToken ct)
	{
		if (row.SecondaryId is not string subjectAccountId)
		{
			return DeliveryOutcome.Unprocessable("A recipient-state task carries no subject account.");
		}

		IReadOnlyList<Audience> audience = await AuthorizedAudienceAsync(
			message, legacyChatId, Sequence(row.ChangeSequence), includeFrozen: false, ct);
		if (audience.Count == 0)
		{
			return DeliveryOutcome.Delivered(0);
		}

		ILookup<string, string> devicesOf = await TrustedDevicesAsync([.. audience.Select(a => a.AccountContextId)], ct);

		int sends = 0;
		foreach (Audience recipient in audience)
		{
			// The ONE projection every surface uses, so a live event and a catch-up page describe the same
			// row identically - including the EventId, which is derived from the state's own version and
			// therefore deduplicates across the two routes.
			MessageRecipientState? state = await projection.RecipientStateAsync(
				recipient.Scope, message.Id, subjectAccountId, ct);
			if (state is null)
			{
				continue;
			}

			string payload = WriteEvent(
				recipient, SyncChangeFactory.RecipientState(recipient.Scope, state, row.ChangeSequence));

			sends += await SendToDevicesAsync(devicesOf[recipient.AccountContextId], payload, ct);
		}

		return DeliveryOutcome.Delivered(sends);
	}

	/// <summary>
	/// Private per-device progress. It is delivered to the SQLite copy that reported it and to nothing
	/// else - not to the person's other devices, and not even to another local instance of the same
	/// registration, because that is a different copy with its own progress. The registration is derived
	/// from the committed row, never from the task.
	///
	/// A FROZEN stint is delivered NOTHING live, exactly like the public mark above: an archive's
	/// authoritative position is fixed at its own bound, and this change was allocated after that bound.
	/// The receipt itself still commits its private row, its journal entry and its answer - confirming an
	/// archive download needs no live echo.
	/// </summary>
	private async Task<DeliveryOutcome> DeliverDeviceStateAsync(
		SyncOutboxEntity row, SyncMessageEntity message, CancellationToken ct)
	{
		if (row.SecondaryId is not string localInstanceId)
		{
			return DeliveryOutcome.Unprocessable("A device-state task carries no local instance.");
		}

		SyncMessageDeviceStateEntity? deviceRow = await db.SyncMessageDeviceStates.AsNoTracking()
			.FirstOrDefaultAsync(s => s.MessageId == message.Id && s.LocalInstanceId == localInstanceId, ct);
		if (deviceRow is null)
		{
			return DeliveryOutcome.Unprocessable("No device state row for this outbox task.");
		}

		// CURRENT trust, re-read now rather than when the task was published: a registration revoked while
		// the task sat in the queue is not addressed at all, and its connections have already been aborted.
		bool trusted = await db.SyncDeviceRegistrations.AsNoTracking().AnyAsync(
			d => d.Id == deviceRow.DeviceRegistrationId
				&& d.AccountId == deviceRow.AccountId
				&& d.TrustState == DeviceTrustState.Trusted
				&& d.Account.EndedAtUtc == null, ct);
		if (!trusted)
		{
			return DeliveryOutcome.Delivered(0);
		}

		// The stint the row itself recorded: confirming the download of an ARCHIVE is exactly the case this
		// private record exists for ("Техническото потвърждение за сваляне на архив на ново устройство
		// обновява частния device sync запис") - but that confirmation is a durable write, not a live hop.
		var stint = await db.SyncMemberships.AsNoTracking()
			.Where(m => m.Id == deviceRow.MembershipId && m.ChatId == message.ChatId)
			.Select(m => new { m.RightsVersion, m.State })
			.FirstOrDefaultAsync(ct);
		if (stint is null)
		{
			return DeliveryOutcome.Unprocessable("The membership of this device state no longer exists.");
		}

		if (stint.State == SyncMembershipState.Frozen)
		{
			// NOTHING is emitted beyond a frozen archive's bound. That bound is the stint's own
			// SyncMembershipMetadataSnapshots.CapturedAtChangeSequence, stamped at freeze time and fixed
			// forever; this change was allocated afterwards, so an event carrying it would name a position
			// the archive's own stream can never legitimately reach. A client applies a live event only at
			// applied+1 and otherwise starts a catch-up, which for a frozen scope is bounded by that same
			// fixed position - so every receipt confirming an archive would cost a readiness flap and a
			// pointless HTTP pass, once per message. Worse, an event landing exactly at bound+1 WOULD be
			// applied and persisted, and the next Resume submitting that position is answered
			// InvalidRequest for the whole batch, which no amount of retrying can clear.
			//
			// The private row, its journal entry and the receipt's own answer are already committed; the
			// archive is confirmed. The outbox row is consumed here as a no-op rather than left unqueued,
			// so an already-queued row from an older build drains the same way.
			return DeliveryOutcome.Delivered(0);
		}

		MessageDeviceState? state = await projection.DeviceStateAsync(
			new DeviceContext(deviceRow.AccountId, deviceRow.DeviceRegistrationId, localInstanceId), message.Id, ct);
		if (state is null)
		{
			return DeliveryOutcome.Unprocessable("No device state row for this outbox task.");
		}

		// Only a still-open stint reaches this far, so the scope a live event is read in is never a frozen
		// one and its upper bound is this change's own position.
		SyncScopeContext scope = ScopeOf(
			message.ChatId, deviceRow.MembershipId, stint.RightsVersion, row.ChangeSequence, isFrozen: false);

		string payload = WriteEvent(
			new Audience(deviceRow.AccountId, scope), SyncChangeFactory.DeviceState(scope, state, row.ChangeSequence));

		// The per-instance group, not the per-registration one: a phone that cleared its local database has
		// a second instance under the same registration, and that copy's progress is not this one's.
		await hub.Clients
			.Group(DeliveryGroups.ForInstance(localInstanceId))
			.SendAsync(LiveEvents.SyncChange, payload, ct);

		return DeliveryOutcome.Delivered(1);
	}

	// ---------------------------------------------------------------- shared addressing

	/// <summary>
	/// Everyone whose own rights permit a message created at this instant, each with the stint those
	/// rights are expressed in. The SAME shared check every other surface asks: a member added later never
	/// reaches back before its own window, a removed member keeps exactly its archive, and a superseded
	/// account context is not a participant at all.
	/// </summary>
	/// <param name="includeFrozen">
	/// False for a status change: an archive is fixed at the removal and must not receive updates produced
	/// after it. True for the message itself, which is what the archive is allowed to contain.
	/// </param>
	private async Task<IReadOnlyList<Audience>> AuthorizedAudienceAsync(
		SyncMessageEntity message, int legacyChatId, string throughChangeSequence, bool includeFrozen, CancellationToken ct)
	{
		IReadOnlyList<ChatParticipantAccess> participants = await authority.DescribeParticipantsAsync(legacyChatId, ct);
		List<ChatParticipantAccess> authorized = [.. participants.Where(p =>
			p.AccountContextId is not null && p.Access.PermitsMessageAt(message.CreatedAtUtc))];
		if (authorized.Count == 0)
		{
			return [];
		}

		Dictionary<string, long> rightsVersions = await db.SyncMemberships.AsNoTracking()
			.Where(m => m.ChatId == message.ChatId)
			.ToDictionaryAsync(m => m.Id, m => m.RightsVersion, StringComparer.Ordinal, ct);

		long position = long.TryParse(throughChangeSequence, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
			? parsed
			: message.ChangeSequence;

		List<Audience> audience = [];
		foreach (ChatParticipantAccess participant in authorized)
		{
			string accountId = participant.AccountContextId!;
			// WHICH stint covers this message is what the recipient's own rights are expressed in - a
			// rejoined member reads an old message through its FROZEN archive stint unless its new period
			// covers the same instant, in which case the still-open one wins (ChatAccess.WindowAt).
			LegacyHistoryWindow? window = participant.Access.WindowAt(message.CreatedAtUtc);
			if (window?.MembershipId is not string membershipId
				|| !rightsVersions.TryGetValue(membershipId, out long rightsVersion))
			{
				// A v1 message only exists in a projected chat with a real stint, so this is a broken record
				// for ONE recipient: skipped with diagnostics, never delivered without a scope.
				logger.LogWarning(
					"Live delivery of message {MessageId} skipped account {AccountContextId}: no membership stint covers the message instant.",
					message.Id, accountId);
				continue;
			}

			if (window.IsFrozen && !includeFrozen)
			{
				continue;
			}

			audience.Add(new Audience(accountId, ScopeOf(message.ChatId, membershipId, rightsVersion, position, window.IsFrozen)));
		}

		return audience;
	}

	/// <summary>
	/// The trusted registrations of each of these accounts, re-read now: a device revoked while the task
	/// sat in the queue is never addressed, and a superseded account context has no devices here at all.
	/// </summary>
	private async Task<ILookup<string, string>> TrustedDevicesAsync(string[] accountIds, CancellationToken ct)
	{
		var devices = await db.SyncDeviceRegistrations.AsNoTracking()
			.Where(d => accountIds.Contains(d.AccountId)
				&& d.TrustState == DeviceTrustState.Trusted
				&& d.Account.EndedAtUtc == null)
			.Select(d => new { d.AccountId, DeviceRegistrationId = d.Id })
			.ToListAsync(ct);

		return devices.ToLookup(d => d.AccountId, d => d.DeviceRegistrationId, StringComparer.Ordinal);
	}

	/// <summary>
	/// Addressed per device registration, not per legacy user id: a registration belongs to exactly one
	/// account context, so this group is fresh-start isolated by construction and a phone's superseded
	/// context can never be reached through its new one. The Redis backplane is what makes the group reach
	/// connections parked on another replica.
	/// </summary>
	private async Task<int> SendToDevicesAsync(IEnumerable<string> deviceRegistrationIds, string payload, CancellationToken ct)
	{
		int sends = 0;
		foreach (string deviceRegistrationId in deviceRegistrationIds)
		{
			await hub.Clients
				.Group(DeliveryGroups.ForDevice(deviceRegistrationId))
				.SendAsync(LiveEvents.SyncChange, payload, ct);
			sends++;
		}

		return sends;
	}

	private static string WriteEvent(Audience recipient, SyncChange change) =>
		JsonSerializer.Serialize(
			new LiveEvent(
				SyncProtocol.Version,
				recipient.AccountContextId,
				Sequence(recipient.Scope.RightsVersion),
				change),
			SyncJson);

	/// <summary>
	/// The scope one live event is read in. Its upper bound is this very change's position: a live event
	/// never claims more than it carries, and the frozen flag is what makes an archive's payload resolve at
	/// its own snapshot rather than at today's state.
	/// </summary>
	private static SyncScopeContext ScopeOf(
		string chatId, string membershipId, long rightsVersion, long position, bool isFrozen) =>
		new(chatId, membershipId, rightsVersion, position, isFrozen, membershipId);

	private static string Sequence(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The SignalR group names v1 live delivery addresses. One place, so the hub's join and the delivery's send cannot drift apart.</summary>
public static class DeliveryGroups
{
	public static string ForDevice(string deviceRegistrationId) =>
		string.Format(LiveEvents.SyncDeviceGroupFormat, deviceRegistrationId);

	/// <summary>One SQLite copy's own address, for the private per-device changes no other device may see.</summary>
	public static string ForInstance(string localInstanceId) =>
		string.Format(LiveEvents.SyncInstanceGroupFormat, localInstanceId);
}
