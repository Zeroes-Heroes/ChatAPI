using System.Globalization;
using System.Net;
using System.Security.Claims;
using Database.Context;
using Database.Entities;
using Database.Entities.Sync;
using Database.Enums;
using Database.Enums.Sync;
using Database.Sequencing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Services.Extensions;
using Services.Hubs;
// Services.Hubs.Models is deliberately NOT imported: it contains a type named MessageStatusUpdate,
// which would collide with the LiveEvents.MessageStatusUpdate event name this file imports statically.
using Services.Membership.Interface;
using Services.Membership.Models;
using Services.Sessions;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Services.Utilities;
using static Services.Utilities.Statics.LiveEvents;

namespace Services.Membership.Service;

/// <summary>
/// See <see cref="IChatMembershipService"/>. Every mutation here follows the same shape:
/// <see cref="TrustedMutation"/> (account -&gt; device -&gt; session locks, so a concurrent revoke of the
/// caller serializes against the write and the broadcast only ever runs after a successful commit)
/// wrapping a body that locks the chat, re-reads the rights under that lock, writes the stint change,
/// its journal entries and its catalog entries, and refuses by throwing - which rolls the whole
/// attempt back rather than leaving a half-applied change behind.
///
/// The one lock order of this module, extending stage 05's: account -&gt; device -&gt; session -&gt; chat
/// (the <c>SyncChatSequenceCounters</c> row) -&gt; the touched accounts' <c>SyncAccountCatalogCounters</c>
/// rows <b>in ascending legacy user id</b>. The last step matters because this is the first module that
/// locks more than one account-scoped counter in a single transaction: two mutations on DIFFERENT chats
/// (so the chat row does not serialize them) with overlapping participants would otherwise take those
/// rows in the order each client happened to list them, and deadlock. Every multi-account loop below
/// therefore iterates a sorted set.
/// </summary>
public sealed class ChatMembershipService(
	AppDbContext db,
	ISyncAccountProvisioningService accounts,
	IPersonalBlockDirectory blocks,
	ISessionTrustChecker trustChecker,
	IAccountContextBoundary contextBoundary,
	IChatAccessAuthority accessAuthority,
	IHubContext<BaseHub> hubContext,
	TimeProvider timeProvider) : IChatMembershipService
{
	public async Task GuardChatCreationAsync(
		int creatorLegacyUserId, IReadOnlyCollection<int> memberLegacyUserIds, CancellationToken ct = default)
	{
		RequireAmbientTransaction(nameof(GuardChatCreationAsync));

		int[] members = [.. memberLegacyUserIds.Distinct().Order()];

		// plan/07-membership.md: "Отказва се дублиран точен състав независимо от името." The advisory lock
		// is the serialization point for that rule - two concurrent creations of the same set cannot both
		// read "no such chat" and then both insert. It is deliberately NOT a unique index over the
		// composition: a group keeps its ChatId through composition changes, so a later removal must never
		// be rejected because the resulting set matches some other chat.
		await SyncChatIdentity.LockKeyAsync(db, SyncChatIdentity.MemberSetKey(members), ct);

		if (await FindChatWithExactActiveMemberSetAsync(members, ct) is int existing)
		{
			throw new DuplicateChatMemberSetException(
				$"A chat with exactly these participants already exists (chat {existing}).");
		}

		// MESSAGE_SYNC_ANALYSIS.md: "Ако А е блокирал Б, Б не може да добавя А" - the creator is the one
		// adding everyone else here.
		IReadOnlySet<int> blockers = await blocks.WhoBlockedAsync(creatorLegacyUserId, members, ct);
		if (blockers.Count > 0)
		{
			throw new ChatMembershipForbiddenException("A chosen participant has blocked you.");
		}
	}

	public async Task ProjectCreatedChatAsync(
		int legacyChatId, string? chatName, IReadOnlyCollection<int> memberLegacyUserIds, CancellationToken ct = default)
	{
		RequireAmbientTransaction(nameof(ProjectCreatedChatAsync));

		DateTime now = timeProvider.GetUtcNow().UtcDateTime;

		await ProjectAsync(legacyChatId, chatName, memberLegacyUserIds, now, ct);
		await db.SaveChangesAsync(ct);
	}

	public Task<Result<ChatMembershipChange>> AddMembersAsync(
		ClaimsPrincipal actingUser, int legacyChatId, IReadOnlyCollection<int> targetLegacyUserIds,
		SharedHistorySelection? sharedHistory = null, CancellationToken ct = default)
	{
		// Distinct (a repeated id is one participant) and ascending - the module's lock order for the
		// per-account rows this add touches, see the type's own doc comment.
		int[] targets = [.. targetLegacyUserIds.Distinct().Order()];
		if (targets.Length == 0)
		{
			return Task.FromResult(Result<ChatMembershipChange>.Failure("No participants were supplied.", HttpStatusCode.BadRequest));
		}

		SharedHistorySelection selection = sharedHistory ?? SharedHistorySelection.None;

		return MutateAsync(actingUser, legacyChatId, (context, token) => AddAsync(context, targets, selection, token), ct);
	}

	public Task<Result<ChatMembershipChange>> RemoveMemberAsync(
		ClaimsPrincipal actingUser, int legacyChatId, int targetLegacyUserId, CancellationToken ct = default) =>
		MutateAsync(actingUser, legacyChatId, (context, token) =>
			// Removing yourself IS leaving, and must be recorded as such: a restored device has to be able
			// to tell a voluntary departure from a removal (docs/sync-contract-v1.md's MembershipEvent), and
			// a client retrying "remove me" must not produce a differently-labelled second outcome.
			targetLegacyUserId == context.ActorLegacyUserId
				? EndOwnMembershipAsync(context, token)
				: RemoveAsync(context, targetLegacyUserId, token), ct);

	public Task<Result<ChatMembershipChange>> LeaveAsync(
		ClaimsPrincipal actingUser, int legacyChatId, CancellationToken ct = default) =>
		MutateAsync(actingUser, legacyChatId, EndOwnMembershipAsync, ct);

	public async Task<IReadOnlyDictionary<int, ChatMembershipView>> DescribeAsync(
		ClaimsPrincipal caller, IReadOnlyCollection<int> legacyChatIds, CancellationToken ct = default)
	{
		Dictionary<int, ChatMembershipView> result = [];
		string? accountId = caller.FindFirstValue(SessionClaimTypes.AccountContextId);
		if (legacyChatIds.Count == 0 || string.IsNullOrEmpty(accountId))
		{
			return result;
		}

		string[] chatIds = [.. legacyChatIds.Select(SyncChatIdentity.IdOf)];

		var chats = await db.SyncChats.AsNoTracking()
			.Where(c => chatIds.Contains(c.Id))
			.Select(c => new { c.Id, c.Kind })
			.ToListAsync(ct);
		if (chats.Count == 0)
		{
			return result;
		}

		// Stints of superseded account contexts are excluded: that phone has started over, so its old
		// context's membership is not part of anybody's current view of the chat (it is retained, never
		// reattached - see Services/Sessions/Service/SyncAccountProvisioningService.cs).
		var stints = await db.SyncMemberships.AsNoTracking()
			.Where(m => chatIds.Contains(m.ChatId) && (m.Account.EndedAtUtc == null || m.AccountId == accountId))
			.Select(m => new { m.ChatId, m.AccountId, m.State, m.CreatedAtUtc, m.EndedAtUtc, LegacyUserId = m.Account.LegacyUserId })
			.ToListAsync(ct);

		foreach (var chat in chats)
		{
			int legacyChatId = int.Parse(chat.Id, CultureInfo.InvariantCulture);
			var chatStints = stints.Where(m => string.Equals(m.ChatId, chat.Id, StringComparison.Ordinal)).ToArray();
			var own = chatStints.Where(m => string.Equals(m.AccountId, accountId, StringComparison.Ordinal)).ToArray();
			if (own.Length == 0)
			{
				continue;
			}

			bool archived = own.All(m => m.State != SyncMembershipState.Active);

			// An active stint sees the current composition; a frozen one sees the composition as of its own
			// removal - "Последващи съобщения, статуси, имена и промени в състава остават извън достъпа му"
			// (MESSAGE_SYNC_ANALYSIS.md). Derived from the stint rows themselves, so no snapshot table is
			// needed for this answer; stage 08 adds the durable frozen metadata snapshots.
			int[] members;
			if (!archived)
			{
				members = [.. chatStints.Where(m => m.State == SyncMembershipState.Active).Select(m => m.LegacyUserId).Distinct()];
			}
			else
			{
				DateTime frozenAt = own.Max(m => m.EndedAtUtc ?? m.CreatedAtUtc);
				members = [.. chatStints
					.Where(m => m.CreatedAtUtc <= frozenAt && (m.EndedAtUtc is null || m.EndedAtUtc > frozenAt))
					.Select(m => m.LegacyUserId)
					.Distinct()];
			}

			result[legacyChatId] = new ChatMembershipView(chat.Kind == SyncChatKind.Group, archived, members);
		}

		return result;
	}

	/// <summary>The state one mutation body works on, all of it already locked and re-read under the chat lock.</summary>
	private sealed record MutationContext(
		ChatEntity LegacyChat,
		SyncChatEntity Chat,
		int ActorLegacyUserId,
		SyncAccountEntity ActorAccount,
		SyncMembershipEntity? ActorStint,
		DateTime Now);

	private async Task<Result<ChatMembershipChange>> MutateAsync(
		ClaimsPrincipal actingUser,
		int legacyChatId,
		Func<MutationContext, CancellationToken, Task<MutationOutcome>> body,
		CancellationToken ct)
	{
		// A superseded account context must not touch the old context's chats at all (stage 06).
		if (await contextBoundary.IsLegacyChatExcludedAsync(actingUser, legacyChatId, ct))
		{
			return Result<ChatMembershipChange>.Failure("Chat not found.", HttpStatusCode.NotFound);
		}

		MutationOutcome? outcome = null;
		TrustCheckResult trust;
		try
		{
			trust = await TrustedMutation.RunAsync(db, trustChecker, actingUser, async () =>
			{
				MutationContext context = await OpenAsync(actingUser, legacyChatId, ct);
				outcome = await body(context, ct);
				await db.SaveChangesAsync(ct);
			}, ct);
		}
		catch (ChatMembershipRefusedException ex)
		{
			// The transaction is already rolled back (the exception escaped TrustedMutation's delegate
			// before its commit), so nothing of this attempt - not even a lazy projection - is persisted.
			return Result<ChatMembershipChange>.Failure(ex.Message, ex.Status);
		}

		if (!trust.IsTrusted)
		{
			return Result<ChatMembershipChange>.Failure("Session is not currently trusted.", HttpStatusCode.Unauthorized);
		}

		MutationOutcome applied = outcome!;
		if (applied.Changed)
		{
			await BroadcastAsync(applied.Change, applied.NotifyLegacyUserIds, ct);
			await BroadcastSharedHistorySeenAsync(applied.Change.ChatId, applied.SharedHistorySeen, ct);
		}

		return Result<ChatMembershipChange>.Success(applied.Change);
	}

	/// <summary><paramref name="Changed"/> is false for an idempotent retry that found the change already applied - nothing was written, and nothing is broadcast a second time.</summary>
	/// <param name="SharedHistorySeen">Stage 08: the logical Seen marks the committed add produced on already-existing messages, to be reported to their authors - "Тази промяна се отразява в разрешените статуси при останалите участници."</param>
	private sealed record MutationOutcome(
		ChatMembershipChange Change,
		IReadOnlyList<int> NotifyLegacyUserIds,
		bool Changed,
		IReadOnlyList<SharedHistorySeenMark> SharedHistorySeen)
	{
		public MutationOutcome(ChatMembershipChange change, IReadOnlyList<int> notify, bool changed)
			: this(change, notify, changed, []) { }
	}

	/// <summary>One author's messages that a just-added member was logically marked as having Seen, for the post-commit status broadcast.</summary>
	private sealed record SharedHistorySeenMark(int SenderLegacyUserId, int RecipientLegacyUserId, int[] MessageIds, DateTime AtUtc);

	private async Task<MutationContext> OpenAsync(ClaimsPrincipal actingUser, int legacyChatId, CancellationToken ct)
	{
		int actorLegacyUserId = actingUser.Id();
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;

		ChatEntity legacyChat = await db.Chats
			.Include(c => c.Users)
			.FirstOrDefaultAsync(c => c.Id == legacyChatId, ct)
			?? throw new ChatMembershipRefusedException("Chat not found.", HttpStatusCode.NotFound);

		string chatId = SyncChatIdentity.IdOf(legacyChatId);

		// Serializes concurrent first-time projections of the same pre-stage-07 chat; taken before the
		// counter row (which does not exist yet for such a chat) and always in this order, so it cannot
		// deadlock against another membership mutation.
		await SyncChatIdentity.LockKeyAsync(db, SyncChatIdentity.ProjectionKey(legacyChatId), ct);

		SyncChatEntity? chat = await db.SyncChats.FirstOrDefaultAsync(c => c.Id == chatId, ct);
		if (chat is null)
		{
			// A chat created before this stage carries no membership history: project exactly what the
			// legacy row says - its current participants, with full history of the chat - so every later
			// decision comes from the membership model instead of the legacy user list.
			chat = await ProjectAsync(legacyChatId, legacyChat.Name, [.. legacyChat.Users.Select(u => u.Id)], now, ct);
			await db.SaveChangesAsync(ct);
		}

		await SyncChatIdentity.TryLockChatAsync(db, chatId, ct);

		// The caller's OWN account context, taken from its authenticated token - never resolved from the
		// legacy user id, which cannot distinguish a phone's current context from a superseded one (the
		// same rule IAccountContextBoundary follows). TrustedMutation has already established that this
		// session is currently trusted, so the context it names is the live one.
		string? claimedAccountId = actingUser.FindFirstValue(SessionClaimTypes.AccountContextId);
		SyncAccountEntity actorAccount = (claimedAccountId is null
			? null
			: await db.SyncAccounts.FirstOrDefaultAsync(a => a.Id == claimedAccountId && a.EndedAtUtc == null, ct))
			?? throw new ChatMembershipForbiddenException("This session has no current account context for this chat.");

		SyncMembershipEntity? actorStint = await db.SyncMemberships
			.FirstOrDefaultAsync(m => m.ChatId == chatId && m.AccountId == actorAccount.Id && m.State == SyncMembershipState.Active, ct);

		return new MutationContext(legacyChat, chat, actorLegacyUserId, actorAccount, actorStint, now);
	}

	private async Task<MutationOutcome> AddAsync(
		MutationContext context, int[] targets, SharedHistorySelection selection, CancellationToken ct)
	{
		RequireGroup(context);
		RequireActiveActor(context);

		if (targets.Contains(context.ActorLegacyUserId))
		{
			throw new ChatMembershipRefusedException("You are already a participant of this chat.", HttpStatusCode.Conflict);
		}

		int known = await db.Users.CountAsync(u => targets.Contains(u.Id), ct);
		if (known != targets.Length)
		{
			throw new ChatMembershipRefusedException("Unknown participant.", HttpStatusCode.NotFound);
		}

		// "Активен участник не се добавя повторно за разширяване на историята" - refused, never a second
		// active stint (also enforced by IX_SyncMemberships_ChatId_AccountId_Active).
		List<int> alreadyActive = await db.SyncMemberships.AsNoTracking()
			.Where(m => m.ChatId == context.Chat.Id
				&& m.State == SyncMembershipState.Active
				&& m.Account.EndedAtUtc == null
				&& targets.Contains(m.Account.LegacyUserId))
			.Select(m => m.Account.LegacyUserId)
			.ToListAsync(ct);
		if (alreadyActive.Count > 0)
		{
			throw new ChatMembershipRefusedException("That participant is already in this chat.", HttpStatusCode.Conflict);
		}

		IReadOnlySet<int> blockers = await blocks.WhoBlockedAsync(context.ActorLegacyUserId, targets, ct);
		if (blockers.Count > 0)
		{
			throw new ChatMembershipForbiddenException("That participant has blocked you.");
		}

		// Stage 08: what this add actually shares - the chosen period, resolved from the SERVER clock,
		// intersected with the history the adder itself may read, under the chat lock this mutation
		// already holds. Computed once for the whole add: every target of one request gets the same
		// period, and a concurrent removal of the adder either commits first (RequireActiveActor above
		// then refuses) or waits for this transaction.
		IReadOnlyList<SharedWindow> sharedWindows = await ResolveSharedWindowsAsync(context, selection, ct);

		List<SharedHistorySeenMark> seenMarks = [];
		foreach (int target in targets)
		{
			SyncAccountEntity account = await accounts.GetOrCreateActiveAccountAsync(db, target, context.Now, ct);

			// Fresh start (stage 06): this phone's CURRENT context is excluded from this legacy chat, so
			// granting it a stint here would hand the successor context rights over a chat its own
			// exclusion snapshot says it must not see. Refused rather than silently granted-and-ignored;
			// re-admitting a context that started over is a later stage's decision (it needs the exclusion
			// itself to be revisited), never a side effect of an ordinary add.
			if (await IsContextExcludedAsync(account.Id, LegacyChatIdOf(context), ct))
			{
				throw new ChatMembershipForbiddenException("That participant cannot be added to this chat.");
			}

			// The legacy row is what the legacy message/status queries and SignalR fan-out read, so a new
			// participant has to appear there too for the addition to take effect immediately.
			if (!context.LegacyChat.Users.Any(u => u.Id == target))
			{
				UserEntity user = await db.Users.FirstAsync(u => u.Id == target, ct);
				context.LegacyChat.Users.Add(user);
			}

			SyncMembershipEntity stint = NewStint(context.Chat.Id, account.Id, context.Now);
			// The PARTICIPATION window always starts at the add: everything older this stint may read is
			// expressed as explicit shared windows below, never by moving this bound back, so "no previous
			// history" and "a shared period with a gap in it" are the same mechanism with different rows.
			stint.LegacyHistoryFromUtc = context.Now;
			stint.AddedByAccountId = context.ActorAccount.Id;
			db.SyncMemberships.Add(stint);
			db.SyncMembershipHistoryGrants.Add(
				new SyncMembershipHistoryGrantEntity(stint.Id, await NextMessageSequenceAsync(context.Chat.Id, ct), null));

			long changeSequence = await AppendMembershipChangeAsync(context.Chat.Id, stint.Id, SyncMembershipEventKind.Added, context.Now, ct);

			foreach (SharedWindow window in sharedWindows)
			{
				db.SyncMembershipSharedHistoryWindows.Add(new SyncMembershipSharedHistoryWindowEntity(
					stint.Id, window.FromUtc, window.ThroughUtc, selection.Period, changeSequence, context.Now));

				await AddSequenceGrantForWindowAsync(context.Chat.Id, stint.Id, window, ct);
			}

			// "Споделените стари съобщения се маркират логически Seen за новия участник, без unread и
			// известия за ново входящо" - inside this same transaction, so a crash cannot leave a member
			// holding shared history that counts as unread.
			seenMarks.AddRange(await MarkSharedHistorySeenAsync(context, target, sharedWindows, ct));

			await AppendCatalogAsync(account.Id, context.Chat.Id, stint.Id, context.Now, ct);
		}

		MutationOutcome outcome = await CompleteAsync(
			context, added: targets, removed: [], left: [], actorUserId: context.ActorLegacyUserId, ct);

		return outcome with { SharedHistorySeen = seenMarks };
	}

	/// <summary>One disjoint window of older history this add shares, already clipped to what the adder may read.</summary>
	private sealed record SharedWindow(DateTime FromUtc, DateTime ThroughUtc);

	/// <summary>
	/// The chosen period intersected with the adder's own permitted history
	/// (MESSAGE_SYNC_ANALYSIS.md: "Споделя се пресечната част на избрания период и историята, разрешена
	/// на добавящия. Недостъпните за него междини остават недостъпни"). The adder's windows are read
	/// through the one shared access check, so a period can never reach into something the adder itself
	/// holds no window for - including the gap of its own rejoin.
	/// </summary>
	private async Task<IReadOnlyList<SharedWindow>> ResolveSharedWindowsAsync(
		MutationContext context, SharedHistorySelection selection, CancellationToken ct)
	{
		if (selection.ResolveFromUtc(context.Now) is not DateTime fromUtc)
		{
			return [];
		}

		ChatAccess adder = await accessAuthority.GetForAccountAsync(context.ActorAccount.Id, LegacyChatIdOf(context), ct);

		List<SharedWindow> clipped = [];
		foreach (LegacyHistoryWindow window in adder.Windows)
		{
			(DateTime From, DateTime Through)? clip = window.ClipTo(fromUtc, context.Now);
			if (clip is not null)
			{
				clipped.Add(new SharedWindow(clip.Value.From, clip.Value.Through));
			}
		}

		// The adder's own windows can overlap (a rejoin whose newly shared period reaches back into its
		// old archive), and overlapping rows would both violate the unique (MembershipId, FromUtc) index
		// and describe the same instant twice. Merged into the disjoint set they actually denote; only a
		// real gap - an instant covered by NO window of the adder - survives as a gap.
		clipped.Sort((left, right) => left.FromUtc.CompareTo(right.FromUtc));

		List<SharedWindow> merged = [];
		foreach (SharedWindow window in clipped)
		{
			if (merged.Count > 0 && window.FromUtc <= merged[^1].ThroughUtc)
			{
				if (window.ThroughUtc > merged[^1].ThroughUtc)
				{
					merged[^1] = merged[^1] with { ThroughUtc = window.ThroughUtc };
				}

				continue;
			}

			merged.Add(window);
		}

		return merged;
	}

	/// <summary>
	/// The sequence-space counterpart of a shared window, written only when the window actually contains
	/// committed <c>SyncMessages</c>. Today it never does - every real message still lives in the legacy
	/// <c>Messages</c> table - but writing it from the same place as the legacy window is what keeps the
	/// two representations from diverging the moment stage 11 starts committing sync messages; inventing
	/// a sequence range for a window with no messages in it would instead durably over-grant.
	/// </summary>
	private async Task AddSequenceGrantForWindowAsync(string chatId, string membershipId, SharedWindow window, CancellationToken ct)
	{
		var bounds = await db.SyncMessages.AsNoTracking()
			.Where(m => m.ChatId == chatId && m.CreatedAtUtc >= window.FromUtc && m.CreatedAtUtc <= window.ThroughUtc)
			.GroupBy(m => 1)
			.Select(g => new { From = g.Min(m => m.MessageSequence), Through = g.Max(m => m.MessageSequence) })
			.FirstOrDefaultAsync(ct);
		if (bounds is null)
		{
			return;
		}

		db.SyncMembershipHistoryGrants.Add(new SyncMembershipHistoryGrantEntity(membershipId, bounds.From, bounds.Through));
	}

	/// <summary>
	/// Marks the shared older messages logically Seen for the added member and reports, per author, what
	/// was marked. Messages the member itself sent are skipped (it is not its own recipient), and a Seen
	/// row that already exists - from an earlier stint whose archive overlaps this period - is left
	/// exactly as it is, which is what keeps "Старият архив ... запазва статусите си" true on a rejoin.
	/// </summary>
	private async Task<IReadOnlyList<SharedHistorySeenMark>> MarkSharedHistorySeenAsync(
		MutationContext context, int targetLegacyUserId, IReadOnlyList<SharedWindow> windows, CancellationToken ct)
	{
		if (windows.Count == 0)
		{
			return [];
		}

		DateTime earliest = windows[0].FromUtc;
		DateTime latest = windows[^1].ThroughUtc;

		var candidates = await db.Messages.AsNoTracking()
			.Where(m => m.ChatId == context.LegacyChat.Id
				&& m.SenderId != targetLegacyUserId
				&& m.CreatedAt >= earliest
				&& m.CreatedAt <= latest
				&& !m.MessageStatusEntities.Any(s => s.ReceiverId == targetLegacyUserId && s.Status == MessageStatus.Seen))
			.Select(m => new { m.Id, m.SenderId, m.CreatedAt })
			.ToListAsync(ct);

		List<SharedHistorySeenMark> marks = [];
		foreach (var bySender in candidates
			.Where(m => windows.Any(w => m.CreatedAt >= w.FromUtc && m.CreatedAt <= w.ThroughUtc))
			.GroupBy(m => m.SenderId))
		{
			int[] messageIds = [.. bySender.Select(m => m.Id)];
			foreach (int messageId in messageIds)
			{
				db.MessagesStatus.Add(new MessageStatusEntity(messageId, targetLegacyUserId, MessageStatus.Seen, context.Now));
			}

			marks.Add(new SharedHistorySeenMark(bySender.Key, targetLegacyUserId, messageIds, context.Now));
		}

		return marks;
	}

	private async Task<MutationOutcome> RemoveAsync(MutationContext context, int targetLegacyUserId, CancellationToken ct)
	{
		RequireGroup(context);
		RequireActiveActor(context);

		SyncMembershipEntity? stint = await db.SyncMemberships
			.FirstOrDefaultAsync(m => m.ChatId == context.Chat.Id
				&& m.State == SyncMembershipState.Active
				&& m.Account.LegacyUserId == targetLegacyUserId
				&& m.Account.EndedAtUtc == null, ct);
		if (stint is null)
		{
			// Already not a participant: an identical retry (or a concurrent removal that won) answers with
			// the current state instead of inventing a second removal or a spurious failure.
			return await CompleteAsync(context, [], [], [], context.ActorLegacyUserId, ct, changed: false);
		}

		await FreezeAsync(context, stint, SyncMembershipEndReason.Removed, context.ActorAccount.Id, ct);

		return await CompleteAsync(context, added: [], removed: [targetLegacyUserId], left: [], actorUserId: context.ActorLegacyUserId, ct);
	}

	private async Task<MutationOutcome> EndOwnMembershipAsync(MutationContext context, CancellationToken ct)
	{
		RequireGroup(context);

		if (context.ActorStint is null)
		{
			// Already left/removed - same answer as the first attempt, nothing written.
			bool everMember = await db.SyncMemberships.AsNoTracking()
				.AnyAsync(m => m.ChatId == context.Chat.Id && m.AccountId == context.ActorAccount.Id, ct);
			if (!everMember)
			{
				throw new ChatMembershipForbiddenException("You are not a participant of this chat.");
			}

			return await CompleteAsync(context, [], [], [], context.ActorLegacyUserId, ct, changed: false);
		}

		await FreezeAsync(context, context.ActorStint, SyncMembershipEndReason.Left, endedByAccountId: null, ct);

		return await CompleteAsync(context, added: [], removed: [], left: [context.ActorLegacyUserId], actorUserId: context.ActorLegacyUserId, ct);
	}

	private async Task FreezeAsync(
		MutationContext context, SyncMembershipEntity stint, SyncMembershipEndReason reason, string? endedByAccountId, CancellationToken ct)
	{
		stint.State = SyncMembershipState.Frozen;
		stint.EndedAtUtc = context.Now;
		stint.EndReason = reason;
		stint.EndedByAccountId = endedByAccountId;
		stint.RightsVersion += 1;

		SyncMembershipEventKind eventKind = reason == SyncMembershipEndReason.Left
			? SyncMembershipEventKind.Left
			: SyncMembershipEventKind.Removed;
		long changeSequence = await AppendMembershipChangeAsync(context.Chat.Id, stint.Id, eventKind, context.Now, ct);

		// Freeze the stint's still-open permitted range at the chat's current end. A range that would end
		// before it starts is not representable (and means "this stint was never granted any committed
		// message"), so it is removed rather than stored as an impossible range.
		long lastMessageSequence = await NextMessageSequenceAsync(context.Chat.Id, ct) - 1;
		List<SyncMembershipHistoryGrantEntity> open = await db.SyncMembershipHistoryGrants
			.Where(g => g.MembershipId == stint.Id && g.ThroughMessageSequence == null)
			.ToListAsync(ct);
		foreach (SyncMembershipHistoryGrantEntity grant in open)
		{
			if (lastMessageSequence >= grant.FromMessageSequence)
			{
				grant.ThroughMessageSequence = lastMessageSequence;
				grant.FrozenAtChangeSequence = changeSequence;
			}
			else
			{
				db.SyncMembershipHistoryGrants.Remove(grant);
			}
		}

		// Stage 08: the archive itself - the chat as it looked and the public statuses as they stood at
		// this exact moment - captured in the SAME transaction, under the SAME chat lock. Anything
		// committed after this point belongs to the chat, not to this archive; nothing committed before
		// it can be missed.
		await CaptureArchiveAsync(context, stint, changeSequence, ct);

		await AppendCatalogAsync(stint.AccountId, context.Chat.Id, stint.Id, context.Now, ct);
	}

	/// <summary>
	/// Freezes what a just-ended stint keeps (plan/08-history-archives.md: "Премахване и напускане
	/// замразяват разрешените съобщения, статуси и метаданни"): the chat's metadata and composition as of
	/// the exit, plus a copy of the public status of every permitted message. The copies exist because
	/// the live rows keep changing for the chat's still-active members - without them the archive would
	/// drift after the fact, and "Последващи съобщения, статуси, имена и промени в състава остават извън
	/// достъпа му" would be false.
	/// </summary>
	private async Task CaptureArchiveAsync(
		MutationContext context, SyncMembershipEntity stint, long changeSequence, CancellationToken ct)
	{
		db.SyncMembershipMetadataSnapshots.Add(new SyncMembershipMetadataSnapshotEntity(
			stint.Id, context.Chat.Name, context.Chat.Kind, changeSequence, context.Now));

		// The composition this archive shows: everyone still active at the exit, plus the departing member
		// itself, recorded as no longer taking part. Exactly the set DescribeAsync derives for a frozen
		// stint today, now durable instead of re-derived from rows that keep changing.
		List<SyncMembershipEntity> remaining = await db.SyncMemberships
			.Where(m => m.ChatId == context.Chat.Id
				&& m.Id != stint.Id
				&& m.State == SyncMembershipState.Active
				&& m.Account.EndedAtUtc == null)
			.ToListAsync(ct);

		db.SyncMembershipMetadataSnapshotMembers.Add(new SyncMembershipMetadataSnapshotMemberEntity(
			stint.Id, context.Chat.Id, stint.AccountId, stint.Id, SyncMembershipState.Frozen));
		foreach (SyncMembershipEntity other in remaining.Where(m => !string.Equals(m.AccountId, stint.AccountId, StringComparison.Ordinal)))
		{
			db.SyncMembershipMetadataSnapshotMembers.Add(new SyncMembershipMetadataSnapshotMemberEntity(
				stint.Id, context.Chat.Id, other.AccountId, other.Id, SyncMembershipState.Active));
		}

		// The messages this stint was permitted: its participation window plus every window of older
		// history that was shared with it. Bounded by the chat's own history - a chat with no messages
		// produces no rows at all.
		List<SharedWindow> windows = [.. (await db.SyncMembershipSharedHistoryWindows.AsNoTracking()
			.Where(w => w.MembershipId == stint.Id)
			.ToListAsync(ct))
			.Select(w => new SharedWindow(w.FromUtc, w.ThroughUtc))];
		windows.Add(new SharedWindow(stint.LegacyHistoryFromUtc ?? DateTime.MinValue, context.Now));

		List<int> permitted = [.. (await db.Messages.AsNoTracking()
			.Where(m => m.ChatId == context.LegacyChat.Id && m.CreatedAt <= context.Now)
			.Select(m => new { m.Id, m.CreatedAt })
			.ToListAsync(ct))
			.Where(m => windows.Any(w => m.CreatedAt >= w.FromUtc && m.CreatedAt <= w.ThroughUtc))
			.Select(m => m.Id)];
		if (permitted.Count == 0)
		{
			return;
		}

		// Every recipient's status, not only the departing member's own: the archive has to keep showing
		// the group's Received/Seen proportions and the author's individual view
		// (MESSAGE_SYNC_ANALYSIS.md: "Премахнатият участник запазва разрешените съобщения, статуси и
		// метаданни в състоянието им към премахването").
		List<MessageStatusEntity> live = await db.MessagesStatus.AsNoTracking()
			.Where(s => permitted.Contains(s.MessageId))
			.ToListAsync(ct);

		foreach (IGrouping<(int MessageId, int ReceiverId), MessageStatusEntity> pair in live.GroupBy(s => (s.MessageId, s.ReceiverId)))
		{
			SyncMembershipLegacyStatusSnapshotEntity snapshot = new(stint.Id, pair.Key.MessageId, pair.Key.ReceiverId, changeSequence)
			{
				// The legacy table is a log with one row per status, so the two kinds are merged into this
				// row's two timestamps; a missing one means that status was never reached by the exit.
				ReceivedAtUtc = pair.Where(s => s.Status == MessageStatus.Received).Select(s => (DateTime?)s.Timestamp).FirstOrDefault(),
				SeenAtUtc = pair.Where(s => s.Status == MessageStatus.Seen).Select(s => (DateTime?)s.Timestamp).FirstOrDefault(),
			};

			db.SyncMembershipLegacyStatusSnapshots.Add(snapshot);
		}
	}

	private async Task<MutationOutcome> CompleteAsync(
		MutationContext context,
		IReadOnlyList<int> added,
		IReadOnlyList<int> removed,
		IReadOnlyList<int> left,
		int actorUserId,
		CancellationToken ct,
		bool changed = true)
	{
		if (changed)
		{
			// One Chat change for the composition itself, so a sync consumer sees the chat's own version
			// move, not only the individual membership events.
			await AppendChangeAsync(context.Chat.Id, SyncChangeKind.Chat, context.Chat.Id, null, context.Now, ct);

			// Flushed before the composition below is read back: the stint rows this mutation just wrote
			// are what the answer (and the broadcast audience) is derived from, and a query goes to the
			// database, not to the change tracker. Still inside the caller's transaction - nothing is
			// visible to anyone else until it commits.
			await db.SaveChangesAsync(ct);
		}

		List<int> activeMembers = await db.SyncMemberships
			.Where(m => m.ChatId == context.Chat.Id && m.State == SyncMembershipState.Active && m.Account.EndedAtUtc == null)
			.Select(m => m.Account.LegacyUserId)
			.ToListAsync(ct);

		// Everyone whose own view of the chat changed: the remaining participants plus the people this
		// mutation just added or ended. The removed subject is included on purpose - it has to learn that
		// it now only holds an archive.
		int[] notify = [.. activeMembers.Concat(added).Concat(removed).Concat(left).Distinct()];

		ChatMembershipChange change = new(
			int.Parse(context.Chat.Id, CultureInfo.InvariantCulture),
			context.Chat.Kind == SyncChatKind.Group,
			IsArchived: !activeMembers.Contains(context.ActorLegacyUserId),
			AddedUserIds: added,
			RemovedUserIds: removed,
			LeftUserIds: left,
			ActiveUserIds: activeMembers,
			ActorUserId: actorUserId);

		return new MutationOutcome(change, notify, changed);
	}

	private async Task BroadcastAsync(ChatMembershipChange change, IReadOnlyList<int> notify, CancellationToken ct)
	{
		// Each copy's IsArchived is true for ITS OWN recipients, so no client has to re-derive its own
		// membership state from an audience-independent flag:
		//   - the participants that remain (and the ones just added) are not archived;
		//   - someone who just left is, and its copy still names the actor, because the actor is itself;
		//   - a removed subject is, and never learns who removed it (docs/sync-contract-v1.md:
		//     ActorAccountContextId is "always null for a Removed event delivered to the removed
		//     subject"), nor the composition the chat keeps without it.
		int[] removed = [.. change.RemovedUserIds];
		int[] left = [.. change.LeftUserIds];
		int[] ordinary = [.. notify.Where(id => !removed.Contains(id) && !left.Contains(id))];

		if (ordinary.Length > 0)
		{
			await SendToLegacyUsersAsync(ordinary, change.ChatId, change with { IsArchived = false }, ct);
		}

		if (left.Length > 0)
		{
			await SendToLegacyUsersAsync(left, change.ChatId, change with { IsArchived = true }, ct);
		}

		if (removed.Length > 0)
		{
			ChatMembershipChange subjectCopy = change with
			{
				ActorUserId = null,
				ActiveUserIds = [],
				IsArchived = true,
			};
			await SendToLegacyUsersAsync(removed, change.ChatId, subjectCopy, ct);
		}
	}

	private Task SendToLegacyUsersAsync(int[] legacyUserIds, int legacyChatId, ChatMembershipChange payload, CancellationToken ct) =>
		SendToLegacyUsersAsync(legacyUserIds, legacyChatId, ChatMembershipChanged, payload, ct);

	private async Task SendToLegacyUsersAsync<T>(int[] legacyUserIds, int legacyChatId, string eventName, T payload, CancellationToken ct)
	{
		// Same fresh-start recipient isolation as every other live send (stage 06): SignalR addresses
		// users by legacy id, which cannot tell a phone's superseded context from its current one.
		LegacyRecipients delivery = await contextBoundary.ResolveLegacyRecipientsAsync(legacyUserIds, legacyChatId, ct);
		if (delivery.UserIds.Count > 0)
		{
			await hubContext.Clients.GetUsersByIds([.. delivery.UserIds]).SendAsync(eventName, payload, ct);
		}

		if (delivery.ConnectionIds.Count > 0)
		{
			await hubContext.Clients.Clients(delivery.ConnectionIds).SendAsync(eventName, payload, ct);
		}
	}

	/// <summary>
	/// Tells each author that the just-added member counts as having Seen their shared older messages
	/// (MESSAGE_SYNC_ANALYSIS.md: "Тази промяна се отразява в разрешените статуси при останалите
	/// участници"). Strictly after the commit, exactly like every other broadcast here, so a rolled-back
	/// add publishes nothing.
	/// </summary>
	private async Task BroadcastSharedHistorySeenAsync(int legacyChatId, IReadOnlyList<SharedHistorySeenMark> marks, CancellationToken ct)
	{
		foreach (SharedHistorySeenMark mark in marks)
		{
			Hubs.Models.MessageStatusUpdateEvent statusUpdate = new(
				legacyChatId, mark.RecipientLegacyUserId, MessageStatus.Seen, mark.AtUtc, mark.MessageIds);

			await SendToLegacyUsersAsync([mark.SenderLegacyUserId], legacyChatId, MessageStatusUpdate, statusUpdate, ct);
		}
	}

	private async Task<SyncChatEntity> ProjectAsync(
		int legacyChatId, string? chatName, IReadOnlyCollection<int> memberLegacyUserIds, DateTime now, CancellationToken ct)
	{
		string chatId = SyncChatIdentity.IdOf(legacyChatId);

		// The canonical participant set: distinct, because a repeated id is one participant and the kind
		// below must be decided over the real set, and ascending, because the loop takes one per-account
		// counter lock per member (the module's lock order - see the type's own doc comment).
		int[] members = [.. memberLegacyUserIds.Distinct().Order()];

		// plan/07-membership.md: a personal chat is created for exactly two friends, a group for more than
		// two - and the kind is then kept for the chat's whole life, whatever happens to its composition.
		SyncChatKind kind = members.Length == 2 ? SyncChatKind.Direct : SyncChatKind.Group;

		SyncChatEntity chat = new(chatId, kind, chatName, now);
		db.SyncChats.Add(chat);
		db.SyncChatSequenceCounters.Add(new SyncChatSequenceCounterEntity(chatId));

		// The counter rows must exist in the database before SyncSequenceAllocator's raw UPDATE can see
		// them, so the chat and its counter are flushed before any sequence is allocated below.
		await db.SaveChangesAsync(ct);

		foreach (int legacyUserId in members)
		{
			SyncAccountEntity account = await accounts.GetOrCreateActiveAccountAsync(db, legacyUserId, now, ct);

			// Fresh start (stage 06): if this phone's CURRENT context is excluded from this legacy chat,
			// the legacy participant row belongs to the context it replaced - projecting a stint for the
			// successor would reattach the old context's membership to it. The row is left exactly as it
			// is; the successor simply has no membership here.
			if (await IsContextExcludedAsync(account.Id, legacyChatId, ct))
			{
				continue;
			}

			// A founding/projected stint has no recorded adder and no lower history bound: it may read this
			// chat from its beginning.
			SyncMembershipEntity stint = NewStint(chatId, account.Id, now);
			db.SyncMemberships.Add(stint);
			db.SyncMembershipHistoryGrants.Add(
				new SyncMembershipHistoryGrantEntity(stint.Id, SyncSequenceConventions.FirstSequence, null));

			await AppendMembershipChangeAsync(chatId, stint.Id, SyncMembershipEventKind.Added, now, ct);
			await AppendCatalogAsync(account.Id, chatId, stint.Id, now, ct);
		}

		await AppendChangeAsync(chatId, SyncChangeKind.Chat, chatId, null, now, ct);

		return chat;
	}

	private static SyncMembershipEntity NewStint(string chatId, string accountId, DateTime now) =>
		// MembershipId is a lowercase UUID per docs/sync-contract-v1.md ("new ... memberships ... use
		// lowercase UUIDs"); a rejoin always gets a new one, never the old stint back.
		new(Guid.NewGuid().ToString("D"), chatId, accountId, SyncMembershipState.Active,
			SyncSequenceConventions.FirstSequence, now);

	private Task<long> AppendMembershipChangeAsync(
		string chatId, string membershipId, SyncMembershipEventKind eventKind, DateTime now, CancellationToken ct) =>
		AppendChangeAsync(chatId, SyncChangeKind.MembershipEvent, membershipId, eventKind, now, ct);

	private async Task<long> AppendChangeAsync(
		string chatId, SyncChangeKind kind, string referenceId, SyncMembershipEventKind? eventKind, DateTime now, CancellationToken ct)
	{
		// Allocated from the chat's counter row, which this transaction holds until commit - so no reader
		// can observe position N+1 while N is still in flight, and a concurrent message write for the same
		// chat is forced into an unambiguous order with this rights change.
		long sequence = await SyncSequenceAllocator.AllocateChangeSequenceAsync(db, chatId, ct);
		db.SyncChatChanges.Add(new SyncChatChangeEntity(chatId, sequence, kind, referenceId, now)
		{
			MembershipEventKind = eventKind,
		});

		return sequence;
	}

	private async Task AppendCatalogAsync(string accountId, string chatId, string membershipId, DateTime now, CancellationToken ct)
	{
		// Takes this account's catalog counter row and holds it until commit. It is the LAST lock of the
		// module's order and the only one a single transaction can take more than one of, so every caller
		// that touches several accounts must reach it in ascending legacy user id - see the type's doc
		// comment. Otherwise two adds on different chats with overlapping participants deadlock here.
		long sequence = await SyncSequenceAllocator.AllocateCatalogSequenceAsync(db, accountId, ct);
		db.SyncCatalogEntries.Add(new SyncCatalogEntryEntity(accountId, sequence, chatId, SyncChangeKind.Membership, now)
		{
			ReferenceId = membershipId,
		});
	}

	private static int LegacyChatIdOf(MutationContext context) => context.LegacyChat.Id;

	/// <summary>Whether this account context is one the stage-06 fresh-start snapshot keeps out of this legacy chat.</summary>
	private Task<bool> IsContextExcludedAsync(string accountId, int legacyChatId, CancellationToken ct) =>
		db.SyncLegacyContextExclusions.AsNoTracking()
			.AnyAsync(e => e.AccountId == accountId && e.LegacyChatId == legacyChatId, ct);

	private async Task<long> NextMessageSequenceAsync(string chatId, CancellationToken ct) =>
		await db.SyncChatSequenceCounters.AsNoTracking()
			.Where(c => c.ChatId == chatId)
			.Select(c => c.NextMessageSequence)
			.SingleAsync(ct);

	private async Task<int?> FindChatWithExactActiveMemberSetAsync(IReadOnlyList<int> members, CancellationToken ct)
	{
		// Candidates: any legacy chat that contains all of these participants. A chat whose ACTIVE set
		// equals this set must contain them all, and the legacy row keeps every past participant (a
		// removal does not delete it, so the archive stays readable) - so this is a superset filter,
		// narrowed below by the membership model. Expressed as a per-chat count of matching
		// participants, which translates to SQL (a local collection's own All(...) does not).
		int[] wantedIds = [.. members];
		var matching = await db.Chats.AsNoTracking()
			.Where(c => c.Users.Any(u => wantedIds.Contains(u.Id)))
			.Select(c => new { c.Id, Matched = c.Users.Count(u => wantedIds.Contains(u.Id)) })
			.ToListAsync(ct);
		List<int> candidates = [.. matching.Where(m => m.Matched == wantedIds.Length).Select(m => m.Id)];
		if (candidates.Count == 0)
		{
			return null;
		}

		string[] candidateChatIds = [.. candidates.Select(SyncChatIdentity.IdOf)];
		var activeStints = await db.SyncMemberships.AsNoTracking()
			.Where(m => candidateChatIds.Contains(m.ChatId)
				&& m.State == SyncMembershipState.Active
				&& m.Account.EndedAtUtc == null)
			.Select(m => new { m.ChatId, LegacyUserId = m.Account.LegacyUserId })
			.ToListAsync(ct);
		var legacyMembers = await db.Chats.AsNoTracking()
			.Where(c => candidates.Contains(c.Id))
			.Select(c => new { c.Id, UserIds = c.Users.Select(u => u.Id).ToList() })
			.ToListAsync(ct);
		// Whether a candidate has a projection at all is decided by the chat row, never by having active
		// stints: a group whose participants have all left is projected and has an EMPTY active set, which
		// must not fall back to its legacy (ever-participant) list and match this request.
		HashSet<string> projected = [.. await db.SyncChats.AsNoTracking()
			.Where(c => candidateChatIds.Contains(c.Id))
			.Select(c => c.Id)
			.ToListAsync(ct)];

		HashSet<int> wanted = [.. members];
		foreach (var candidate in legacyMembers)
		{
			string chatId = SyncChatIdentity.IdOf(candidate.Id);
			HashSet<int> effective = projected.Contains(chatId)
				? activeStints
					.Where(m => string.Equals(m.ChatId, chatId, StringComparison.Ordinal))
					.Select(m => m.LegacyUserId)
					.ToHashSet()
				// No projection (a chat created before this stage) - its legacy participant list is its
				// current composition.
				: candidate.UserIds.ToHashSet();

			if (effective.SetEquals(wanted))
			{
				return candidate.Id;
			}
		}

		return null;
	}

	private static void RequireGroup(MutationContext context)
	{
		if (context.Chat.Kind != SyncChatKind.Group)
		{
			// "Личният чат ... запазва личния си тип": a personal chat is never turned into a group, and
			// nobody is removed from it - blocking/removing a contact (stage 09) is the path for that.
			throw new ChatMembershipRefusedException("A personal chat's participants cannot be changed.", HttpStatusCode.Conflict);
		}
	}

	private static void RequireActiveActor(MutationContext context)
	{
		if (context.ActorStint is null)
		{
			// Includes the frozen-archive holder and the member of a group with zero active members: the
			// archive is read-only (plan/07-membership.md).
			throw new ChatMembershipForbiddenException("Only an active participant can change this chat's participants.");
		}
	}

	private void RequireAmbientTransaction(string member)
	{
		if (db.Database.CurrentTransaction is null)
		{
			throw new InvalidOperationException(
				$"{member} must run inside the caller's own explicit transaction so the chat row/advisory locks it takes " +
				"are held until the caller's write commits.");
		}
	}
}
