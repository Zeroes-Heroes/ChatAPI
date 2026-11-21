using System.Net;
using System.Security.Claims;
using Database.Context;
using Database.Entities;
using Database.Entities.Sync;
using Database.Enums;
using Microsoft.EntityFrameworkCore;
using Services.Authorship.Interface;
using Services.Authorship.Models;
using Services.History.Interface;
using Services.History.Models;
using Services.Membership.Interface;
using Services.Membership.Models;
using Services.Sessions;
using Services.Statuses.Interface;
using Services.Statuses.Models;
using Services.Utilities;

namespace Services.History.Service;

/// <summary>
/// See <see cref="IChatHistoryService"/>. Reads only; the one thing it writes is the private
/// per-device archive record, which no public status or unread computation ever reads. It takes no
/// chat lock on purpose: it can neither change a public status nor allocate a sequence, so it has
/// nothing to serialize against a send - and holding the chat row across a history page would stall
/// every writer in that chat for the length of a download.
/// </summary>
public sealed class ChatHistoryService(
	AppDbContext db, IChatAccessAuthority accessAuthority, IMessageAuthorshipAuthority authorship,
	IMessageStatusService statuses, TimeProvider timeProvider) : IChatHistoryService
{
	/// <summary>Upper bound on one page, so a client cannot ask for an unbounded scan (docs/sync-contract-v1.md: pages are bounded and must advance or terminate).</summary>
	public const int MaxPageSize = 100;

	public async Task<Result<ChatHistoryPage>> ReadAsync(
		ClaimsPrincipal caller, int legacyChatId, int? beforeMessageId, int limit, bool missingOnThisDeviceOnly, CancellationToken ct = default)
	{
		ChatAccess access = await accessAuthority.GetAsync(caller, legacyChatId, ct);
		if (!access.CanRead)
		{
			// Indistinguishable from a chat that does not exist: an authorization failure must not confirm
			// the existence of someone else's chat (and an excluded fresh-start chat is not this context's
			// at all).
			return Result<ChatHistoryPage>.Failure("Chat not found.", HttpStatusCode.NotFound);
		}

		int pageSize = Math.Clamp(limit <= 0 ? MaxPageSize : limit, 1, MaxPageSize);
		string? localInstanceId = caller.FindFirstValue(SessionClaimTypes.LocalInstanceId);
		string[] frozenStints = FrozenStintsOf(access);

		// One bounded keyset scan, newest first. Permission is per message (a window boundary is a time,
		// not an id), so the page is filtered afterwards and may come back smaller than pageSize - but
		// NextBeforeMessageId is the lowest id actually SCANNED, so the caller always advances past a
		// stretch of inaccessible messages instead of re-reading it forever.
		IQueryable<MessageEntity> scan = db.Messages.AsNoTracking()
			.Where(m => m.ChatId == legacyChatId && (beforeMessageId == null || m.Id < beforeMessageId));

		// "Skip what this device already stored" has to happen IN the scan, not after it: a restore that
		// scans pageSize ids and then throws the confirmed ones away spends its whole page budget
		// re-reading what it already has, so an archive longer than that budget never reaches its older
		// messages. Pre-filtering is only EQUIVALENT to the per-message rule below while the caller holds
		// no open window at all - then every window is frozen, so "has a private record" and "frozen and
		// has a private record" select the same rows. With an open window (a rejoin whose newly shared
		// period overlaps the old archive) the post-filter below stays the only judge, and the overlap is
		// answered with today's state as the stage requires.
		if (missingOnThisDeviceOnly && !string.IsNullOrEmpty(localInstanceId) && frozenStints.Length > 0
			&& access.Windows.All(w => w.IsFrozen))
		{
			string instanceId = localInstanceId;
			scan = scan.Where(m => !db.SyncArchiveDeviceDeliveries
				.Any(d => d.LegacyMessageId == m.Id && d.LocalInstanceId == instanceId && frozenStints.Contains(d.MembershipId)));
		}

		var scanned = await scan
			.OrderByDescending(m => m.Id)
			.Take(pageSize)
			.Select(m => new { m.Id, m.SenderId, m.Content, m.CreatedAt })
			.ToListAsync(ct);

		// The private progress of THIS SQLite copy, restricted to the ids actually scanned: it only feeds
		// the per-message flag and the per-message rule below, both of which are about this page, so there
		// is no reason to pull a whole archive's worth of rows into memory for one page.
		HashSet<int> storedHere = await StoredOnThisDeviceAsync(frozenStints, localInstanceId, [.. scanned.Select(m => m.Id)], ct);

		List<(int Id, int SenderId, string Content, DateTime CreatedAt, LegacyHistoryWindow Window)> permitted = [];
		foreach (var message in scanned)
		{
			if (access.WindowAt(message.CreatedAt) is not LegacyHistoryWindow window)
			{
				continue;
			}

			if (missingOnThisDeviceOnly && window.IsFrozen && storedHere.Contains(message.Id))
			{
				continue;
			}

			permitted.Add((message.Id, message.SenderId, message.Content, message.CreatedAt, window));
		}

		IReadOnlyDictionary<int, MessageStatusSummary> resolved = await ResolveStatusesAsync(legacyChatId, permitted, ct);

		// Stage 09: the author rights a future edit/delete will need, decided by the one authority rather
		// than by each surface comparing sender ids. A frozen window still produces them - that is the
		// "собствено съобщение в архив" case.
		IReadOnlySet<int> revisable = authorship.RevisableByAuthor(
			caller, access, [.. permitted.Select(m => new AuthoredMessage(m.Id, m.SenderId, m.CreatedAt))]);

		List<ChatHistoryMessage> messages = [.. permitted.Select(m => new ChatHistoryMessage(
			m.Id,
			m.SenderId,
			m.Content,
			m.CreatedAt,
			m.Window.IsFrozen,
			storedHere.Contains(m.Id),
			revisable.Contains(m.Id),
			resolved.TryGetValue(m.Id, out MessageStatusSummary? s) ? s.Statuses : Array.Empty<ChatHistoryStatus>(),
			resolved.TryGetValue(m.Id, out MessageStatusSummary? r) ? r.RecipientCount : 0))];

		// Complete exactly when the scan itself ran out of rows - never when the FILTERED page is empty,
		// which happens routinely for a stretch that falls in an inaccessible gap.
		int? next = scanned.Count < pageSize ? null : scanned[^1].Id;

		return Result<ChatHistoryPage>.Success(
			new ChatHistoryPage(legacyChatId, access.State == ChatAccessState.Frozen, messages, next));
	}

	public async Task<Result<ArchiveStoreResult>> ConfirmArchiveStoredAsync(
		ClaimsPrincipal caller, int legacyChatId, IReadOnlyCollection<int> legacyMessageIds, CancellationToken ct = default)
	{
		ChatAccess access = await accessAuthority.GetAsync(caller, legacyChatId, ct);
		if (!access.CanRead)
		{
			return Result<ArchiveStoreResult>.Failure("Chat not found.", HttpStatusCode.NotFound);
		}

		string? localInstanceId = caller.FindFirstValue(SessionClaimTypes.LocalInstanceId);
		if (string.IsNullOrEmpty(localInstanceId))
		{
			// The record is per SQLite copy; without one there is nothing to record progress FOR, and
			// inventing an instance id would make another device's progress indistinguishable from this
			// one's.
			return Result<ArchiveStoreResult>.Failure("This session has no local instance.", HttpStatusCode.BadRequest);
		}

		// The instance must really belong to this caller's own account context. The claim is server-issued,
		// but this is the row a foreign key will point at, and it is the one check that keeps a confirmation
		// from being recorded against somebody else's device.
		string? accountId = caller.FindFirstValue(SessionClaimTypes.AccountContextId);
		bool ownsInstance = accountId is not null && await db.SyncLocalInstances.AsNoTracking()
			.AnyAsync(i => i.Id == localInstanceId && i.DeviceRegistration.AccountId == accountId, ct);
		if (!ownsInstance)
		{
			return Result<ArchiveStoreResult>.Failure("This session has no local instance.", HttpStatusCode.BadRequest);
		}

		int[] requested = [.. legacyMessageIds.Distinct()];
		if (requested.Length == 0)
		{
			return Result<ArchiveStoreResult>.Success(new ArchiveStoreResult(legacyChatId, 0, 0, 0));
		}

		if (requested.Length > MaxPageSize)
		{
			// Bounded like the read side: a confirmation answers for one page that was just read, and an
			// unbounded id list would turn one request into an arbitrarily large scan and insert.
			return Result<ArchiveStoreResult>.Failure(
				$"At most {MaxPageSize} messages can be confirmed in one request.", HttpStatusCode.BadRequest);
		}

		var candidates = await db.Messages.AsNoTracking()
			.Where(m => m.ChatId == legacyChatId && requested.Contains(m.Id))
			.Select(m => new { m.Id, m.CreatedAt })
			.ToListAsync(ct);

		// Only messages that fall in a FROZEN window get a private record: a message in an active window is
		// ordinary current content whose delivery the existing public Received path owns.
		List<(int MessageId, string MembershipId)> archived = [];
		foreach (var candidate in candidates)
		{
			if (access.WindowAt(candidate.CreatedAt) is { IsFrozen: true, MembershipId: not null } window)
			{
				archived.Add((candidate.Id, window.MembershipId));
			}
		}

		if (archived.Count == 0)
		{
			return Result<ArchiveStoreResult>.Success(new ArchiveStoreResult(legacyChatId, 0, 0, requested.Length));
		}

		// Ascending by message id so two concurrent confirmations of the same page take the same rows in
		// the same order: ON CONFLICT DO NOTHING waits on a conflicting uncommitted insert, and two
		// statements inserting the same rows in opposite orders could wait on each other.
		archived.Sort((left, right) => left.MessageId.CompareTo(right.MessageId));
		string[] stintOfRow = [.. archived.Select(a => a.MembershipId)];
		int[] messageOfRow = [.. archived.Select(a => a.MessageId)];
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;

		// ONE conflict-tolerant statement rather than read-then-insert. Two confirmations of the same ids
		// genuinely overlap - the archive can be opened twice in a row, and a retry after a lost response
		// is sent while the first one may still be in flight - and a read-then-insert would let both read
		// an "existing" set without the rows and the loser fail on
		// PK_SyncArchiveDeviceDeliveries (MembershipId, LocalInstanceId, LegacyMessageId), i.e. a 500 for
		// what is by definition the same outcome. The row a racer wrote is identical to this one's, so
		// losing the race IS the idempotent success this endpoint promises. Rows actually inserted are
		// Recorded; everything else was already recorded. No lock: the row is private to this instance.
		int recorded = await db.Database.ExecuteSqlInterpolatedAsync(
			$"""
			INSERT INTO "SyncArchiveDeviceDeliveries" ("MembershipId", "LocalInstanceId", "LegacyMessageId", "StoredAtUtc")
			SELECT confirmed.membership_id, {localInstanceId}, confirmed.legacy_message_id, {now}
			FROM unnest({stintOfRow}::text[], {messageOfRow}::integer[]) AS confirmed(membership_id, legacy_message_id)
			ON CONFLICT ("MembershipId", "LocalInstanceId", "LegacyMessageId") DO NOTHING
			""", ct);

		return Result<ArchiveStoreResult>.Success(new ArchiveStoreResult(
			legacyChatId, recorded, archived.Count - recorded, requested.Length - archived.Count));
	}

	/// <summary>The ended stints this caller holds - the only ones a private per-device archive record can belong to.</summary>
	private static string[] FrozenStintsOf(ChatAccess access) =>
		[.. access.Windows
			.Where(w => w.IsFrozen && w.MembershipId is not null)
			.Select(w => w.MembershipId!)
			.Distinct()];

	private async Task<HashSet<int>> StoredOnThisDeviceAsync(
		string[] frozenStints, string? localInstanceId, int[] legacyMessageIds, CancellationToken ct)
	{
		if (string.IsNullOrEmpty(localInstanceId) || frozenStints.Length == 0 || legacyMessageIds.Length == 0)
		{
			return [];
		}

		return [.. await db.SyncArchiveDeviceDeliveries.AsNoTracking()
			.Where(d => d.LocalInstanceId == localInstanceId
				&& frozenStints.Contains(d.MembershipId)
				&& legacyMessageIds.Contains(d.LegacyMessageId))
			.Select(d => d.LegacyMessageId)
			.ToListAsync(ct)];
	}

	/// <summary>
	/// Two resolution rules, unchanged in spirit from stage 08: a frozen window answers from its own
	/// stint's snapshot ("Резолюцията на архивните метаданни и статуси е към посочения snapshot, не към
	/// днешното състояние"), everything else from today's state. Stage 10 makes the second half the
	/// status module's answer rather than a second reading of the legacy log, and adds the proportions'
	/// denominator - the people with the right to each message, author excluded.
	/// </summary>
	private async Task<IReadOnlyDictionary<int, MessageStatusSummary>> ResolveStatusesAsync(
		int legacyChatId,
		IReadOnlyList<(int Id, int SenderId, string Content, DateTime CreatedAt, LegacyHistoryWindow Window)> permitted,
		CancellationToken ct)
	{
		Dictionary<int, MessageStatusSummary> result = [];
		if (permitted.Count == 0)
		{
			return result;
		}

		int[] liveIds = [.. permitted.Where(m => !m.Window.IsFrozen).Select(m => m.Id)];
		int[] frozenIds = [.. permitted.Where(m => m.Window.IsFrozen).Select(m => m.Id)];

		if (liveIds.Length > 0)
		{
			IReadOnlyDictionary<int, MessageStatusView> live = await statuses.DescribeAsync(legacyChatId, liveIds, ct);
			foreach (KeyValuePair<int, MessageStatusView> entry in live)
			{
				result[entry.Key] = new MessageStatusSummary(
					entry.Value.RecipientCount,
					[.. entry.Value.Recipients
						.Where(r => r.ReceivedAtUtc is not null || r.SeenAtUtc is not null)
						.Select(ToStatus)]);
			}
		}

		if (frozenIds.Length > 0)
		{
			// Keyed by the stint each message is frozen under, so a chat the caller has left twice answers
			// each stretch from ITS OWN archive rather than from the latest snapshot.
			Dictionary<int, string> stintOf = [];
			Dictionary<int, int> senderOf = [];
			foreach (var message in permitted.Where(m => m.Window.IsFrozen && m.Window.MembershipId is not null))
			{
				stintOf[message.Id] = message.Window.MembershipId!;
				senderOf[message.Id] = message.SenderId;
			}

			string[] stints = [.. stintOf.Values.Distinct()];
			List<SyncMembershipLegacyStatusSnapshotEntity> frozen = await db.SyncMembershipLegacyStatusSnapshots.AsNoTracking()
				.Where(s => stints.Contains(s.MembershipId) && frozenIds.Contains(s.LegacyMessageId))
				.ToListAsync(ct);

			foreach (IGrouping<int, SyncMembershipLegacyStatusSnapshotEntity> byMessage in frozen
				.Where(s => stintOf.TryGetValue(s.LegacyMessageId, out string? stint) && string.Equals(stint, s.MembershipId, StringComparison.Ordinal))
				.GroupBy(s => s.LegacyMessageId))
			{
				// The archive's own denominator. The freeze writes one row per person ENTITLED to that
				// message back then - including the ones that had reached no status at all, whose row
				// carries two null timestamps (ChatMembershipService.CaptureArchiveAsync) - so the row
				// count IS "хората с право върху съответното съобщение". The author is excluded here as
				// well as there: a stray self-status row must not inflate a proportion.
				int senderId = senderOf.TryGetValue(byMessage.Key, out int sender) ? sender : 0;
				result[byMessage.Key] = new MessageStatusSummary(
					byMessage.Select(s => s.RecipientLegacyUserId).Where(id => id != senderId).Distinct().Count(),
					[.. byMessage
						.Where(s => s.RecipientLegacyUserId != senderId)
						.Select(ToStatus)
						.Where(s => s is not null)
						.Select(s => s!)]);
			}
		}

		return result;
	}

	/// <summary>What one message's statuses resolved to, before they are put on the page.</summary>
	private sealed record MessageStatusSummary(int RecipientCount, IReadOnlyList<ChatHistoryStatus> Statuses);

	/// <summary>
	/// The legacy-shaped projection of one person's logical state: Seen implies Received, so the page
	/// carries the highest one reached, exactly as the pre-stage-10 log did.
	/// </summary>
	private static ChatHistoryStatus ToStatus(RecipientStatus status) =>
		status.SeenAtUtc is DateTime seenAt
			? new ChatHistoryStatus(status.RecipientLegacyUserId, MessageStatus.Seen, seenAt)
			: new ChatHistoryStatus(status.RecipientLegacyUserId, MessageStatus.Received, status.ReceivedAtUtc!.Value);

	private static ChatHistoryStatus? ToStatus(SyncMembershipLegacyStatusSnapshotEntity snapshot) =>
		snapshot.SeenAtUtc is DateTime seenAt
			? new ChatHistoryStatus(snapshot.RecipientLegacyUserId, MessageStatus.Seen, seenAt)
			: snapshot.ReceivedAtUtc is DateTime receivedAt
				? new ChatHistoryStatus(snapshot.RecipientLegacyUserId, MessageStatus.Received, receivedAt)
				: null;
}
