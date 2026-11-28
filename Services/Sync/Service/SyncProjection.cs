using System.Security.Cryptography;
using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Services.Authorship.Interface;
using Services.Crypto;
using Services.Keys;
using Services.Keys.Interface;
using Services.Keys.Models;
using Services.Sessions;
using Services.Statuses.Models;
using Services.Sync.Contracts;
using Services.Sync.Models;
using static Services.Sync.Service.SyncWire;

namespace Services.Sync.Service;

/// <summary>
/// Turns committed server state into the v1 payloads a page carries: chat/membership/unread metadata,
/// whole encrypted messages with their key grants and author verification keys, and the per-person and
/// per-device statuses that belong to them.
///
/// Two rules run through all of it:
/// <list type="bullet">
/// <item>A FROZEN scope is resolved at its own snapshot rows (metadata, composition, statuses) and at
/// its own frozen bound - "Resolve archive metadata/status at the named snapshot, not today's state".</item>
/// <item>Key material is materialized only after the caller's right to the exact message has been
/// decided by the caller of this class, is unwrapped through the key module with the message's own
/// context, and is zeroed before the method returns.</item>
/// </list>
/// It decides no rights of its own: every method answers about messages its caller has already selected
/// from the stint's own history grants.
/// </summary>
public sealed class SyncProjection(
	AppDbContext db,
	IAuthorKeyRegistry authorKeys,
	IMessageKeyProvisioningService messageKeys,
	IProtectedMessageKeyStore keyStore,
	ILogger<SyncProjection> logger)
{
	/// <summary>One message and the status changes a page reports with it - added to a page whole, never split.</summary>
	public sealed record MessageGroup(long MessageSequence, IReadOnlyList<SyncChange> Changes);

	/// <summary>
	/// The permitted message ranges of one stint, in MESSAGE-sequence space. This - not the legacy time
	/// window - is the authority for v1 content, as stage 12 recorded it would have to become once a
	/// stage read history in sequence space.
	/// </summary>
	public async Task<List<(long From, long Through, long? FrozenAt)>> GrantsAsync(
		string membershipId, long openRangeEnd, CancellationToken ct)
	{
		var rows = await db.SyncMembershipHistoryGrants.AsNoTracking()
			.Where(g => g.MembershipId == membershipId)
			.OrderBy(g => g.FromMessageSequence)
			.Select(g => new { g.FromMessageSequence, g.ThroughMessageSequence, g.FrozenAtChangeSequence })
			.ToListAsync(ct);

		// A still-open range ends at whatever this read's own upper bound is; a frozen one keeps its own.
		return [.. rows.Select(r => (r.FromMessageSequence, r.ThroughMessageSequence ?? openRangeEnd, r.FrozenAtChangeSequence))];
	}

	/// <summary>The largest message sequence that lies at or below a scope's fixed change bound: the upper end of everything this session may ever read in it.</summary>
	public async Task<long> MessageBoundAsync(SyncScopeContext scope, CancellationToken ct) =>
		await db.SyncMessages.AsNoTracking()
			.Where(m => m.ChatId == scope.ChatId && m.ChangeSequence <= scope.ThroughChangeSequence)
			.Select(m => (long?)m.MessageSequence)
			.MaxAsync(ct) ?? 0;

	/// <summary>
	/// The whole encrypted messages of one page, each with its key grant, its author verification key and
	/// the statuses that belong to it. Batched deliberately: a page is one bounded portion, not N round
	/// trips.
	/// </summary>
	public async Task<IReadOnlyList<MessageGroup>> DescribeMessagesAsync(
		SyncScopeContext scope, IReadOnlyList<SyncMessageEntity> messages, SyncCallerIdentity caller,
		bool includeStatuses, CancellationToken ct)
	{
		if (messages.Count == 0)
		{
			return [];
		}

		string[] ids = [.. messages.Select(m => m.Id)];

		// Through the key module's own storage seam, in one bounded read per page - never a query of this
		// module's own against the protected-key table.
		IReadOnlyDictionary<string, SyncMessageKeyEntity> keys = await keyStore.FindManyAsync(db, ids, ct);

		// A delta page reports a status change as its own journal entry at its own position, so only a
		// full restoration carries the statuses alongside the message they belong to.
		ILookup<string, MessageRecipientState> recipientStates = includeStatuses
			? await RecipientStatesAsync(scope, ids, ct)
			: EmptyLookup<MessageRecipientState>();

		ILookup<string, MessageDeviceState> deviceStates = includeStatuses
			? await DeviceStatesAsync(caller.Device, ids, ct)
			: EmptyLookup<MessageDeviceState>();

		Dictionary<string, byte[]> spkiCache = new(StringComparer.Ordinal);
		List<MessageGroup> groups = [];

		foreach (SyncMessageEntity message in messages)
		{
			AuthorKeyReference reference = new(
				message.SenderAccountId, message.ChatId, message.AuthorKeyId, message.AuthorKeyVersion);

			string referenceKey = $"{reference.AccountContextId}|{reference.ChatId}|{reference.AuthorKeyId}|{reference.AuthorKeyVersion}";
			if (!spkiCache.TryGetValue(referenceKey, out byte[]? spki))
			{
				// The server-authorized registry, never the package's own header - the same source live
				// delivery uses.
				byte[]? found = await authorKeys.FindSpkiAsync(reference, ct);
				if (found is null)
				{
					// An isolated unreadable record: reported with diagnostics and skipped, never delivered
					// without the key that proves its authorship ("Пропуснат изолиран повреден ... запис се
					// отчита с диагностика и не блокира останалия набор").
					logger.LogWarning(
						"Sync page skipped message {MessageId}: its author key reference is not bound to a key.", message.Id);
					continue;
				}

				spki = found;
				spkiCache[referenceKey] = spki;
			}

			if (!keys.TryGetValue(message.Id, out SyncMessageKeyEntity? keyRow))
			{
				logger.LogWarning("Sync page skipped message {MessageId}: no protected key row.", message.Id);
				continue;
			}

			MessageDelivery? delivery = Deliver(message, keyRow, reference, spki, scope);
			if (delivery is null)
			{
				continue;
			}

			// The statuses of a message are reported at the message's own position: a page carries whole
			// messages, so a device never holds a status whose body it has not been given.
			List<SyncChange> changes = [SyncChangeFactory.Message(scope, delivery, message.ChangeSequence)];
			changes.AddRange(recipientStates[message.Id]
				.Select(state => SyncChangeFactory.RecipientState(scope, state, message.ChangeSequence)));
			changes.AddRange(deviceStates[message.Id]
				.Select(state => SyncChangeFactory.DeviceState(scope, state, message.ChangeSequence)));

			groups.Add(new MessageGroup(message.MessageSequence, changes));
		}

		return groups;
	}

	/// <summary>Unwraps the content key for one already-authorized message and leaves no plaintext copy behind.</summary>
	private MessageDelivery? Deliver(
		SyncMessageEntity message, SyncMessageKeyEntity keyRow, AuthorKeyReference reference, byte[] spki, SyncScopeContext scope)
	{
		byte[] messageKey;
		try
		{
			messageKey = messageKeys.UnwrapForAuthorizedDelivery(keyRow, new MessageKeyAccessContext(message.Id, message.ChatId));
		}
		catch (Exception ex) when (ex is KeyContextMismatchException or KeyProtectionIntegrityException or MasterSecretUnavailableException)
		{
			// One broken or temporarily unopenable key record must not fail the whole page: it is reported
			// and left out, and the rest of the permitted set still arrives.
			logger.LogWarning(ex, "Sync page skipped message {MessageId}: its protected key could not be opened.", message.Id);
			return null;
		}

		try
		{
			return new MessageDelivery(
				new MessageEnvelope(
					message.Id,
					message.ChatId,
					message.SenderAccountId,
					Seq(message.MessageSequence),
					// A message is immutable, so its version is its own position in the chat's change stream.
					Seq(message.ChangeSequence),
					RfcTimestamp.Format(message.CreatedAtUtc),
					message.SignedPackage),
				new MessageKeyGrant(message.Id, keyRow.KeyVersion, Base64Url.Encode(messageKey)),
				new AuthorVerificationKey(reference, Base64Url.Encode(spki)),
				scope.Scope);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(messageKey);
		}
	}

	/// <summary>
	/// Per-person logical statuses: today's rows for an active scope, and the stint's own frozen copies
	/// for an archive - "Публичните отметки ... в замразения чат остават непроменени", so an archive's
	/// view cannot drift with the chat's later life.
	///
	/// Exactly ONE record per (message, person) either way. The stored rows are per STINT, and a rejoined
	/// person legitimately holds more than one of them for the same message, so the live branch folds them
	/// through <see cref="PublicRecipientState"/> - the same fold the frozen capture uses. The frozen
	/// branch needs no fold: a snapshot is keyed by (membership, message, recipient) and the scope names
	/// one membership.
	/// </summary>
	private async Task<ILookup<string, MessageRecipientState>> RecipientStatesAsync(
		SyncScopeContext scope, string[] messageIds, CancellationToken ct)
	{
		if (scope.IsFrozen)
		{
			var frozen = await db.SyncMembershipMessageStatusSnapshots.AsNoTracking()
				.Where(s => s.MembershipId == scope.MembershipId && messageIds.Contains(s.MessageId))
				.Select(s => new { s.MessageId, s.RecipientAccountId, s.ReceivedAtUtc, s.SeenAtUtc, s.SeenReason })
				.ToListAsync(ct);

			return frozen.ToLookup(
				s => s.MessageId,
				s => new MessageRecipientState(
					s.MessageId,
					s.RecipientAccountId,
					// A frozen copy never changes again, so it carries the one version an archive has.
					"0",
					s.ReceivedAtUtc is DateTime received ? RfcTimestamp.Format(received) : null,
					Seen(s.SeenAtUtc, s.SeenReason)),
				StringComparer.Ordinal);
		}

		var live = await db.SyncMessageRecipientStates.AsNoTracking()
			.Where(s => s.ChatId == scope.ChatId && messageIds.Contains(s.MessageId))
			.Select(s => new { s.MessageId, s.AccountId, s.Version, s.ReceivedAtUtc, s.SeenAtUtc, s.SeenReason })
			.ToListAsync(ct);

		// One canonical state per (message, person), never one per stint row: a public mark is one fact
		// about one person, and emitting the rows of a rejoined person separately would let a surface
		// show the same person twice - with the second record contradicting the first.
		return live
			.GroupBy(s => (s.MessageId, s.AccountId))
			.Select(rows =>
			{
				PublicRecipientState state = PublicRecipientState.Fold(rows.Select(
					r => new PublicRecipientState(r.Version, r.ReceivedAtUtc, r.SeenAtUtc, r.SeenReason)));

				return new MessageRecipientState(
					rows.Key.MessageId,
					rows.Key.AccountId,
					Seq(state.Version),
					state.ReceivedAtUtc is DateTime received ? RfcTimestamp.Format(received) : null,
					Seen(state.SeenAtUtc, state.SeenReason));
			})
			.ToLookup(s => s.MessageId, StringComparer.Ordinal);
	}

	/// <summary>
	/// One recipient's state for one message, resolved the same way as a whole page's: today's folded
	/// state, or the archive's frozen copy. The pick below is unambiguous rather than arbitrary -
	/// <see cref="RecipientStatesAsync"/> answers at most one record per person per message.
	/// </summary>
	public async Task<MessageRecipientState?> RecipientStateAsync(
		SyncScopeContext scope, string messageId, string recipientAccountId, CancellationToken ct)
	{
		ILookup<string, MessageRecipientState> states = await RecipientStatesAsync(scope, [messageId], ct);
		return states[messageId].FirstOrDefault(s =>
			string.Equals(s.RecipientAccountContextId, recipientAccountId, StringComparison.Ordinal));
	}

	/// <summary>This device's own progress for one message, or null when it has reported nothing about it.</summary>
	public Task<MessageDeviceState?> OwnDeviceStateAsync(SyncCallerIdentity caller, string messageId, CancellationToken ct) =>
		DeviceStateAsync(caller.Device, messageId, ct);

	/// <summary>
	/// One named device's own progress for one message, for the surface that is not the device itself -
	/// live delivery, which addresses the registration/local instance the receipt came from. Same single
	/// mapping as the page's, so a live event and a page cannot describe the same row differently.
	/// </summary>
	public async Task<MessageDeviceState?> DeviceStateAsync(DeviceContext device, string messageId, CancellationToken ct)
	{
		ILookup<string, MessageDeviceState> states = await DeviceStatesAsync(device, [messageId], ct);
		return states[messageId].FirstOrDefault();
	}

	private static RecipientSeen? Seen(DateTime? seenAtUtc, SyncSeenReason? reason) =>
		seenAtUtc is DateTime seen && reason is SyncSeenReason seenReason
			? new RecipientSeen(
				seenReason switch
				{
					SyncSeenReason.HistoryShared => SeenReason.HistoryShared,
					SyncSeenReason.OwnSend => SeenReason.OwnSend,
					_ => SeenReason.Observed,
				},
				RfcTimestamp.Format(seen))
			: null;

	/// <summary>
	/// Only the CALLER's own device rows. Device progress is per registration and local instance and is
	/// nobody else's business - the person's public marks are the recipient states above.
	/// </summary>
	private async Task<ILookup<string, MessageDeviceState>> DeviceStatesAsync(
		DeviceContext device, string[] messageIds, CancellationToken ct)
	{
		var states = await db.SyncMessageDeviceStates.AsNoTracking()
			.Where(s => s.LocalInstanceId == device.LocalInstanceId
				&& s.DeviceRegistrationId == device.DeviceRegistrationId
				&& messageIds.Contains(s.MessageId))
			.Select(s => new { s.MessageId, s.Version, s.CiphertextStoredAtUtc, s.ReceivedAtUtc, s.ObservedSeenAtUtc })
			.ToListAsync(ct);

		return states.ToLookup(
			s => s.MessageId,
			s => new MessageDeviceState(
				s.MessageId,
				device,
				Seq(s.Version),
				s.CiphertextStoredAtUtc is DateTime stored ? RfcTimestamp.Format(stored) : null,
				s.ReceivedAtUtc is DateTime received ? RfcTimestamp.Format(received) : null,
				s.ObservedSeenAtUtc is DateTime observed ? RfcTimestamp.Format(observed) : null),
			StringComparer.Ordinal);
	}

	/// <summary>The chat as this scope is entitled to see it: today's state for an active stint, the stint's own exit snapshot for an archive.</summary>
	public async Task<ChatSnapshot?> ChatSnapshotAsync(SyncScopeContext scope, CancellationToken ct)
	{
		SyncChatEntity? chat = await db.SyncChats.AsNoTracking().FirstOrDefaultAsync(c => c.Id == scope.ChatId, ct);
		if (chat is null)
		{
			return null;
		}

		MembershipSnapshot? membership = await MembershipSnapshotAsync(scope, ct);
		if (membership is null)
		{
			return null;
		}

		UnreadState unread = await UnreadAsync(scope, ct);
		List<string> members;
		string name;
		ChatKind kind;

		if (scope.IsFrozen)
		{
			SyncMembershipMetadataSnapshotEntity? snapshot = await db.SyncMembershipMetadataSnapshots.AsNoTracking()
				.FirstOrDefaultAsync(s => s.MembershipId == scope.MembershipId, ct);

			members = await db.SyncMembershipMetadataSnapshotMembers.AsNoTracking()
				.Where(m => m.MembershipId == scope.MembershipId)
				.Select(m => m.ParticipantAccountId)
				.ToListAsync(ct);

			name = snapshot?.ChatName ?? chat.Name ?? string.Empty;
			kind = Kind(snapshot?.ChatKindAtSnapshot ?? chat.Kind);
		}
		else
		{
			members = await db.SyncMemberships.AsNoTracking()
				.Where(m => m.ChatId == scope.ChatId && m.State == SyncMembershipState.Active && m.Account.EndedAtUtc == null)
				.Select(m => m.AccountId)
				.Distinct()
				.ToListAsync(ct);

			name = chat.Name ?? string.Empty;
			kind = Kind(chat.Kind);
		}

		// The newest message this scope may actually read, which is what a catalog entry previews. Only
		// the sequence space: a legacy message has no v1 identity and is served by the stage-08 legacy
		// history surface, not by this stream.
		long messageBound = await MessageBoundAsync(scope, ct);
		var last = await PermittedMessagesAsync(scope, from: 0, through: messageBound, limit: 1, newestFirst: true, ct);

		return new ChatSnapshot(
			chat.Id,
			// The chat's own fixed position in this session: a later page can never replace it with an
			// older view, and a frozen scope's version is the position it was frozen at.
			Seq(scope.ThroughChangeSequence),
			kind,
			name,
			members,
			[membership],
			unread,
			last.Count > 0 ? last[0].Id : null,
			last.Count > 0 ? RfcTimestamp.Format(last[0].CreatedAtUtc) : null);
	}

	/// <summary>
	/// The caller's OWN stint, with the disjoint ranges it may read. Other participants' grants are
	/// deliberately absent: a chat snapshot says who is in the chat, never what each of them may read.
	/// </summary>
	public async Task<MembershipSnapshot?> MembershipSnapshotAsync(SyncScopeContext scope, CancellationToken ct)
	{
		SyncMembershipEntity? stint = await db.SyncMemberships.AsNoTracking()
			.FirstOrDefaultAsync(m => m.Id == scope.MembershipId && m.ChatId == scope.ChatId, ct);

		if (stint is null)
		{
			return null;
		}

		long messageBound = await MessageBoundAsync(scope, ct);
		List<(long From, long Through, long? FrozenAt)> grants = await GrantsAsync(scope.MembershipId, messageBound, ct);

		List<HistoryRange> ranges =
		[
			.. grants
				.Where(g => g.Through >= g.From)
				.Select(g => new HistoryRange(
					Seq(g.From),
					Seq(Math.Min(g.Through, messageBound)),
					// A frozen range's snapshot identity IS its own stint (see
					// SyncMembershipHistoryGrantEntity); a still-open one is resolved at this session's
					// fixed view, which is the only snapshot it has.
					g.FrozenAt is null ? scope.SnapshotId : scope.MembershipId,
					g.FrozenAt is long frozen ? Seq(frozen) : null)),
		];

		return new MembershipSnapshot(
			scope.Scope,
			Seq(stint.RightsVersion),
			stint.State == SyncMembershipState.Frozen ? MembershipState.Frozen : MembershipState.Active,
			ranges,
			scope.IsFrozen ? scope.MembershipId : scope.SnapshotId);
	}

	/// <summary>
	/// This stint's unread counter. A stint that has never been incremented has no row; the answer is
	/// then an explicit zero at version 0, because a catalog entry always states the count it knows.
	/// </summary>
	public async Task<UnreadState> UnreadAsync(SyncScopeContext scope, CancellationToken ct)
	{
		var row = await db.SyncUnreadStates.AsNoTracking()
			.Where(u => u.MembershipId == scope.MembershipId)
			.Select(u => new
			{
				u.Version,
				u.Count,
				u.OldestUnreadMessageId,
				u.LatestUnreadMessageId,
				u.LatestUnreadCreatedAtUtc,
				u.Membership.AccountId,
			})
			.FirstOrDefaultAsync(ct);

		string accountId = row?.AccountId ?? await db.SyncMemberships.AsNoTracking()
			.Where(m => m.Id == scope.MembershipId)
			.Select(m => m.AccountId)
			.FirstAsync(ct);

		return new UnreadState(
			scope.ChatId,
			accountId,
			scope.MembershipId,
			Seq(row?.Version ?? 0),
			Seq(row?.Count ?? 0),
			row?.OldestUnreadMessageId,
			row?.LatestUnreadMessageId,
			row?.LatestUnreadCreatedAtUtc is DateTime latest ? RfcTimestamp.Format(latest) : null);
	}

	/// <summary>
	/// One durable membership event, with the actor resolved per RECIPIENT: "ActorAccountContextId is
	/// null whenever unauthorized, and always null for a Removed event delivered to the removed subject"
	/// - the removed person learns that they were removed, never by whom.
	/// </summary>
	public async Task<MembershipEvent?> MembershipEventAsync(
		SyncScopeContext scope, string membershipId, SyncMembershipEventKind kind, DateTime occurredAtUtc,
		string recipientAccountId, CancellationToken ct)
	{
		var stint = await db.SyncMemberships.AsNoTracking()
			.Where(m => m.Id == membershipId && m.ChatId == scope.ChatId)
			.Select(m => new { m.AccountId, m.AddedByAccountId, m.EndedByAccountId })
			.FirstOrDefaultAsync(ct);

		if (stint is null)
		{
			return null;
		}

		string? actor = kind switch
		{
			SyncMembershipEventKind.Added => stint.AddedByAccountId,
			SyncMembershipEventKind.Removed => stint.EndedByAccountId,
			// A voluntary departure is the subject's own act, which is exactly what distinguishes it from
			// a removal even on a newly restored device.
			_ => stint.AccountId,
		};

		if (kind == SyncMembershipEventKind.Removed && string.Equals(recipientAccountId, stint.AccountId, StringComparison.Ordinal))
		{
			actor = null;
		}

		return new MembershipEvent(
			scope.Scope,
			kind switch
			{
				SyncMembershipEventKind.Left => MembershipEventKind.Left,
				SyncMembershipEventKind.Removed => MembershipEventKind.Removed,
				_ => MembershipEventKind.Added,
			},
			stint.AccountId,
			actor,
			RfcTimestamp.Format(occurredAtUtc));
	}

	/// <summary>
	/// The membership events a full restoration recovers for this scope, derived from the STINT ROWS -
	/// not from the change journal, which is a liveness mechanism and may be pruned. Each stint carries
	/// its own join (and, once ended, its own departure) with the actor that caused it, so the events are
	/// recoverable for as long as the memberships themselves are: "MembershipEvent is a durable,
	/// history-recoverable system event ... permitted system events remain recoverable in history."
	///
	/// A caller sees its own stint's events - including its own removal, which is exactly the event the
	/// contract insists is kept for the subject - and the events of other participants that happened
	/// while the caller itself was taking part.
	/// </summary>
	public async Task<IReadOnlyList<(string MembershipId, MembershipEvent Event)>> RecoverableMembershipEventsAsync(
		SyncScopeContext scope, string recipientAccountId, CancellationToken ct)
	{
		var stints = await db.SyncMemberships.AsNoTracking()
			.Where(m => m.ChatId == scope.ChatId)
			.Select(m => new
			{
				m.Id,
				m.AccountId,
				m.CreatedAtUtc,
				m.EndedAtUtc,
				m.EndReason,
				m.AddedByAccountId,
				m.EndedByAccountId,
			})
			.ToListAsync(ct);

		var own = stints.FirstOrDefault(s => s.Id == scope.MembershipId);
		if (own is null)
		{
			return [];
		}

		bool Visible(string membershipId, DateTime at) =>
			string.Equals(membershipId, scope.MembershipId, StringComparison.Ordinal)
			|| (at >= own.CreatedAtUtc && (own.EndedAtUtc is null || at <= own.EndedAtUtc));

		List<(string MembershipId, DateTime At, MembershipEvent Event)> events = [];
		foreach (var stint in stints)
		{
			if (Visible(stint.Id, stint.CreatedAtUtc))
			{
				events.Add((stint.Id, stint.CreatedAtUtc, new MembershipEvent(
					scope.Scope, MembershipEventKind.Added, stint.AccountId, stint.AddedByAccountId, RfcTimestamp.Format(stint.CreatedAtUtc))));
			}

			if (stint.EndedAtUtc is not DateTime endedAt || !Visible(stint.Id, endedAt))
			{
				continue;
			}

			bool removed = stint.EndReason == SyncMembershipEndReason.Removed;
			events.Add((stint.Id, endedAt, new MembershipEvent(
				scope.Scope,
				removed ? MembershipEventKind.Removed : MembershipEventKind.Left,
				stint.AccountId,
				// "always null for a Removed event delivered to the removed subject" - the removed person
				// learns that they were removed, never by whom. A voluntary departure is the subject's own
				// act, so its actor is the subject and the two stay distinguishable.
				removed
					? (string.Equals(recipientAccountId, stint.AccountId, StringComparison.Ordinal) ? null : stint.EndedByAccountId)
					: stint.AccountId,
				RfcTimestamp.Format(endedAt))));
		}

		return [.. events.OrderBy(e => e.At).ThenBy(e => e.MembershipId, StringComparer.Ordinal).Select(e => (e.MembershipId, e.Event))];
	}

	/// <summary>
	/// The messages of one scope inside its own history grants, ordered by message sequence. This is the
	/// only place a v1 message is selected for reading, so the grant ranges are the one authority for
	/// what a stint may see - an added member never reaches before its own range, and a frozen one never
	/// past the position it was frozen at.
	/// </summary>
	public async Task<List<SyncMessageEntity>> PermittedMessagesAsync(
		SyncScopeContext scope, long from, long through, int limit, bool newestFirst, CancellationToken ct)
	{
		List<(long From, long Through, long? FrozenAt)> grants = await GrantsAsync(scope.MembershipId, through, ct);
		if (grants.Count == 0 || through <= 0)
		{
			return [];
		}

		// One bounded query PER permitted range, merged in memory: a stint holds a handful of disjoint
		// ranges (a rejoin's archive plus its newly shared period), and the gap between them - the period
		// nobody shared - simply matches nothing, so it is absent rather than filled in.
		List<SyncMessageEntity> found = [];
		foreach ((long From, long Through, long? FrozenAt) grant in grants)
		{
			long lower = Math.Max(from, grant.From);
			long upper = Math.Min(through, grant.Through);
			if (upper < lower)
			{
				continue;
			}

			IQueryable<SyncMessageEntity> query = db.SyncMessages.AsNoTracking()
				.Where(m => m.ChatId == scope.ChatId
					&& m.MessageSequence >= lower
					&& m.MessageSequence <= upper
					&& m.ChangeSequence <= scope.ThroughChangeSequence);

			found.AddRange(await (newestFirst
					? query.OrderByDescending(m => m.MessageSequence)
					: query.OrderBy(m => m.MessageSequence))
				.Take(limit)
				.ToListAsync(ct));
		}

		return
		[
			.. (newestFirst
					? found.OrderByDescending(m => m.MessageSequence)
					: found.OrderBy(m => m.MessageSequence))
				.Take(limit),
		];
	}

	private static ChatKind Kind(SyncChatKind kind) => kind == SyncChatKind.Group ? ChatKind.Group : ChatKind.Direct;

	private static ILookup<string, T> EmptyLookup<T>() => Array.Empty<T>().ToLookup(_ => string.Empty, StringComparer.Ordinal);
}
