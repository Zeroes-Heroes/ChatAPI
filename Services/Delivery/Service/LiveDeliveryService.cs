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
using Services.Utilities.Statics;

namespace Services.Delivery.Service;

/// <summary>See <see cref="ILiveDeliveryService"/>.</summary>
public sealed class LiveDeliveryService(
	AppDbContext db,
	IChatAccessAuthority authority,
	IAuthorKeyRegistry authorKeys,
	IMessageKeyProvisioningService messageKeys,
	IProtectedMessageKeyStore keyStore,
	IHubContext<BaseHub> hub,
	ILogger<LiveDeliveryService> logger) : ILiveDeliveryService
{
	/// <summary>The v1 encoding, built once - the same JSON the hub's own v1 methods speak, never the legacy MessagePack resolver.</summary>
	private static readonly JsonSerializerOptions SyncJson = SyncProtocol.CreateJsonOptions();

	public async Task<DeliveryOutcome> DeliverAsync(DeliveryTask task, CancellationToken ct = default)
	{
		SyncMessageEntity? message = await db.SyncMessages.AsNoTracking()
			.FirstOrDefaultAsync(m => m.Id == task.MessageId && m.ChatId == task.ChatId, ct);
		if (message is null)
		{
			// The outbox row and the message commit together and neither is deletable, so this is a broken
			// record rather than a race - kept with its reason instead of retried forever.
			return DeliveryOutcome.Unprocessable("No message row for this outbox task.");
		}

		if (!int.TryParse(message.ChatId, NumberStyles.None, CultureInfo.InvariantCulture, out int legacyChatId))
		{
			return DeliveryOutcome.Unprocessable("Chat id does not name a legacy chat.");
		}

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

		// The SAME shared check every other surface asks, applied to the instant the message was created:
		// a member added later never reaches back before its own window, a removed member keeps exactly
		// its archive, and a superseded account context is not a participant at all.
		IReadOnlyList<ChatParticipantAccess> participants = await authority.DescribeParticipantsAsync(legacyChatId, ct);
		List<ChatParticipantAccess> authorized = [.. participants.Where(p =>
			p.AccountContextId is not null && p.Access.PermitsMessageAt(message.CreatedAtUtc))];
		if (authorized.Count == 0)
		{
			return DeliveryOutcome.Delivered(0);
		}

		string[] accountIds = [.. authorized.Select(p => p.AccountContextId!)];

		Dictionary<string, long> rightsVersions = await db.SyncMemberships.AsNoTracking()
			.Where(m => m.ChatId == message.ChatId)
			.ToDictionaryAsync(m => m.Id, m => m.RightsVersion, StringComparer.Ordinal, ct);

		// CURRENT trust, re-read now rather than when the task was published: only a registration that is
		// trusted at this moment is addressed at all, so a device revoked while the task sat in the queue
		// is never handed a key grant - and its connections have already been aborted by the revocation.
		var devices = await db.SyncDeviceRegistrations.AsNoTracking()
			.Where(d => accountIds.Contains(d.AccountId)
				&& d.TrustState == DeviceTrustState.Trusted
				&& d.Account.EndedAtUtc == null)
			.Select(d => new { d.AccountId, DeviceRegistrationId = d.Id })
			.ToListAsync(ct);
		if (devices.Count == 0)
		{
			return DeliveryOutcome.Delivered(0);
		}

		ILookup<string, string> devicesOf = devices.ToLookup(d => d.AccountId, d => d.DeviceRegistrationId, StringComparer.Ordinal);

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
			foreach (ChatParticipantAccess participant in authorized)
			{
				string accountId = participant.AccountContextId!;
				// WHICH stint covers this message is what the recipient's own rights are expressed in - a
				// rejoined member reads an old message through its FROZEN archive stint, not through the new one.
				LegacyHistoryWindow? window = participant.Access.WindowAt(message.CreatedAtUtc);
				if (window?.MembershipId is not string membershipId
					|| !rightsVersions.TryGetValue(membershipId, out long rightsVersion))
				{
					// A v1 message only exists in a projected chat with a real stint, so this is a broken
					// record for ONE recipient: skipped with diagnostics, never delivered without a scope.
					logger.LogWarning(
						"Live delivery of message {MessageId} skipped account {AccountContextId}: no membership stint covers the message instant.",
						message.Id, accountId);
					continue;
				}

				MembershipScope scope = new(message.ChatId, membershipId, Sequence(rightsVersion));
				string payload = JsonSerializer.Serialize(
					new LiveEvent(
						SyncProtocol.Version,
						accountId,
						Sequence(rightsVersion),
						new SyncChange(
							task.EventId,
							new StreamPosition(StreamKind.Chat, message.ChatId, membershipId, task.ChangeSequence),
							scope,
							ChangeKind.Message,
							Chat: null,
							Message: new MessageDelivery(envelope, grant, verificationKey, scope),
							RecipientState: null,
							DeviceState: null,
							Unread: null,
							Membership: null,
							MembershipEvent: null)),
					SyncJson);

				// Addressed per device registration, not per legacy user id: a registration belongs to
				// exactly one account context, so this group is fresh-start isolated by construction and a
				// phone's superseded context can never be reached through its new one. The Redis backplane is
				// what makes the group reach connections parked on another replica.
				foreach (string deviceRegistrationId in devicesOf[accountId])
				{
					await hub.Clients
						.Group(DeliveryGroups.ForDevice(deviceRegistrationId))
						.SendAsync(LiveEvents.SyncChange, payload, ct);
					sends++;
				}
			}

			return DeliveryOutcome.Delivered(sends);
		}
		finally
		{
			// The transient key existed only to be granted; nothing in this scope keeps it afterwards.
			CryptographicOperations.ZeroMemory(messageKey);
		}
	}

	private static string Sequence(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The SignalR group names v1 live delivery addresses. One place, so the hub's join and the delivery's send cannot drift apart.</summary>
public static class DeliveryGroups
{
	public static string ForDevice(string deviceRegistrationId) =>
		string.Format(LiveEvents.SyncDeviceGroupFormat, deviceRegistrationId);
}
