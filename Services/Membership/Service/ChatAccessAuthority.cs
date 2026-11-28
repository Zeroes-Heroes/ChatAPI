using System.Security.Claims;
using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Contacts.Interface;
using Services.Extensions;
using Services.Membership.Interface;
using Services.Membership.Models;
using Services.Sessions;
using Services.Sessions.Interface;

namespace Services.Membership.Service;

/// <summary>
/// See <see cref="IChatAccessAuthority"/>. Reads the committed membership stints of one account
/// context and turns them into the union of permitted windows; it never writes, and it never decides
/// anything from a user id alone when an account context is available.
///
/// Chats with no sync projection - every chat created before stage 07 - keep their pre-stage-07
/// answer (a legacy participant is an unrestricted active member). That is the honest reading of the
/// data, not a bypass: those rows carry no membership history at all, and the first membership
/// mutation on such a chat projects it (<see cref="ChatMembershipService.ProjectCreatedChatAsync"/>'s
/// lazy counterpart) so every later answer comes from the membership model.
/// </summary>
public sealed class ChatAccessAuthority(
	AppDbContext db, IAccountContextBoundary boundary, IPersonalRelationDirectory relations) : IChatAccessAuthority
{
	public async Task<ChatAccess> GetAsync(ClaimsPrincipal caller, int legacyChatId, CancellationToken ct = default)
	{
		IReadOnlyDictionary<int, ChatAccess> map = await GetManyAsync(caller, [legacyChatId], ct);
		return map.TryGetValue(legacyChatId, out ChatAccess? access) ? access : ChatAccess.Denied;
	}

	public async Task<IReadOnlyDictionary<int, ChatAccess>> GetManyAsync(
		ClaimsPrincipal caller, IReadOnlyCollection<int> legacyChatIds, CancellationToken ct = default)
	{
		Dictionary<int, ChatAccess> result = [];
		if (legacyChatIds.Count == 0)
		{
			return result;
		}

		string? accountId = caller.FindFirstValue(SessionClaimTypes.AccountContextId);
		if (string.IsNullOrEmpty(accountId))
		{
			// Every app-session token carries this claim (Services.Sessions.Authorization.AppSessionRequirement
			// rejects a token without it before any of these surfaces is reached), so this is fail-closed
			// rather than a fallback path: nothing without an account context may read a chat.
			foreach (int chatId in legacyChatIds)
			{
				result[chatId] = ChatAccess.Denied;
			}

			return result;
		}

		// Fresh start (stage 06): a superseded context's chats are not this context's history at all.
		IReadOnlySet<int> excluded = await boundary.ExcludedLegacyChatIdsAsync(caller, ct);

		int[] considered = [.. legacyChatIds.Where(id => !excluded.Contains(id))];
		foreach (int chatId in legacyChatIds.Where(excluded.Contains))
		{
			result[chatId] = ChatAccess.Denied;
		}

		if (considered.Length > 0)
		{
			foreach (KeyValuePair<int, ChatAccess> entry in await ResolveAsync(accountId, caller.Id(), considered, ct))
			{
				result[entry.Key] = entry.Value;
			}
		}

		return result;
	}

	public async Task<ChatAccess> GetForAccountAsync(string accountContextId, int legacyChatId, CancellationToken ct = default)
	{
		int? legacyUserId = await db.SyncAccounts.AsNoTracking()
			.Where(a => a.Id == accountContextId)
			.Select(a => (int?)a.LegacyUserId)
			.SingleOrDefaultAsync(ct);
		if (legacyUserId is null)
		{
			return ChatAccess.Denied;
		}

		IReadOnlyDictionary<int, ChatAccess> map = await ResolveAsync(accountContextId, legacyUserId.Value, [legacyChatId], ct);
		return map.TryGetValue(legacyChatId, out ChatAccess? access) ? access : ChatAccess.Denied;
	}

	public async Task<ChatAccess> LockChatAndAuthorizeAsync(ClaimsPrincipal caller, int legacyChatId, CancellationToken ct = default)
	{
		if (db.Database.CurrentTransaction is null)
		{
			throw new InvalidOperationException(
				$"{nameof(LockChatAndAuthorizeAsync)} requires the caller's own explicit transaction; locking the chat " +
				"outside one would release the row lock before the caller's write commits.");
		}

		// The exact row SyncSequenceAllocator takes for a membership change, so the two serialize. A chat
		// with no projection has no such row and therefore no serialization point - the answer below is
		// then the unrestricted legacy one, which no membership change can invalidate because the first
		// membership change on that chat projects it (under the projection lock) before changing anything.
		await SyncChatIdentity.TryLockChatAsync(db, SyncChatIdentity.IdOf(legacyChatId), ct);

		return await GetAsync(caller, legacyChatId, ct);
	}

	public async Task<IReadOnlyList<ChatParticipantAccess>> DescribeParticipantsAsync(int legacyChatId, CancellationToken ct = default)
	{
		string chatId = SyncChatIdentity.IdOf(legacyChatId);

		// Superseded account contexts are excluded (plan/06 fresh start): a phone that started over keeps
		// its stints on the ended context, which can never read this chat again - so it holds no right to
		// any message in it. Counting it would both inflate the group proportions (the same person would
		// appear twice once the fresh context rejoins) and raise an unread counter nobody can ever read.
		// The same filter the archive's own composition snapshot applies.
		List<SyncMembershipEntity> stints = await db.SyncMemberships.AsNoTracking()
			.Where(m => m.ChatId == chatId && m.Account.EndedAtUtc == null)
			.ToListAsync(ct);

		if (stints.Count == 0)
		{
			// Nothing came back for one of two very different reasons, and only the PROJECTION can tell
			// them apart: a chat created before stage 07 has no membership history at all and keeps its
			// unrestricted legacy reading, while a projected chat whose every stint belongs to a
			// superseded context simply has no participants and must not fall through to it. Asked only
			// here, because any live stint already proves the projection exists.
			if (await db.SyncChats.AsNoTracking().AnyAsync(c => c.Id == chatId, ct))
			{
				return [];
			}

			// Its legacy participant list IS its composition, each one unrestricted - the same fallback
			// every other answer here gives.
			List<int> legacyParticipants = await db.Chats.AsNoTracking()
				.Where(c => c.Id == legacyChatId)
				.SelectMany(c => c.Users.Select(u => u.Id))
				.ToListAsync(ct);

			return [.. legacyParticipants.Select(id => new ChatParticipantAccess(id, null, ChatAccess.UnboundedActive))];
		}

		string[] stintIds = [.. stints.Select(m => m.Id)];
		List<SyncMembershipSharedHistoryWindowEntity> sharedWindows = await db.SyncMembershipSharedHistoryWindows.AsNoTracking()
			.Where(w => stintIds.Contains(w.MembershipId))
			.ToListAsync(ct);

		string[] accountIds = [.. stints.Select(m => m.AccountId).Distinct()];
		Dictionary<string, int> legacyUserOf = await db.SyncAccounts.AsNoTracking()
			.Where(a => accountIds.Contains(a.Id))
			.ToDictionaryAsync(a => a.Id, a => a.LegacyUserId, StringComparer.Ordinal, ct);

		List<ChatParticipantAccess> participants = [];
		foreach (IGrouping<string, SyncMembershipEntity> byAccount in stints.GroupBy(m => m.AccountId, StringComparer.Ordinal))
		{
			if (!legacyUserOf.TryGetValue(byAccount.Key, out int legacyUserId))
			{
				continue;
			}

			// The personal-correspondence gate is deliberately NOT applied: it only ever narrows the right
			// to create NEW messages, and this answer is about who has rights to messages that already
			// exist - a blocked pair keeps receiving each other's status updates (stage 09).
			participants.Add(new ChatParticipantAccess(legacyUserId, byAccount.Key, BuildAccess([.. byAccount], sharedWindows)));
		}

		return participants;
	}

	public async Task<IReadOnlyList<int>> ActiveLegacyRecipientsAsync(
		int legacyChatId, IReadOnlyCollection<int> candidateLegacyUserIds, CancellationToken ct = default)
	{
		int[] candidates = [.. candidateLegacyUserIds];
		if (candidates.Length == 0)
		{
			return [];
		}

		string chatId = SyncChatIdentity.IdOf(legacyChatId);
		bool projected = await db.SyncChats.AsNoTracking().AnyAsync(c => c.Id == chatId, ct);
		if (!projected)
		{
			// Pre-stage-07 chat: its participants are exactly the legacy ones the caller already resolved.
			return candidates;
		}

		// Only a phone's CURRENT context can be addressed by legacy user id, which is all SignalR and the
		// device-token table know; a superseded context has no live session of its own either way.
		List<int> active = await db.SyncMemberships.AsNoTracking()
			.Where(m => m.ChatId == chatId
				&& m.State == SyncMembershipState.Active
				&& m.Account.EndedAtUtc == null
				&& candidates.Contains(m.Account.LegacyUserId))
			.Select(m => m.Account.LegacyUserId)
			.Distinct()
			.ToListAsync(ct);

		HashSet<int> allowed = [.. active];
		return [.. candidates.Where(allowed.Contains)];
	}

	private async Task<IReadOnlyDictionary<int, ChatAccess>> ResolveAsync(
		string accountId, int legacyUserId, IReadOnlyCollection<int> legacyChatIds, CancellationToken ct)
	{
		string[] chatIds = [.. legacyChatIds.Select(SyncChatIdentity.IdOf)];

		List<SyncMembershipEntity> stints = await db.SyncMemberships.AsNoTracking()
			.Where(m => m.AccountId == accountId && chatIds.Contains(m.ChatId))
			.ToListAsync(ct);

		// Stage 08: the older history that was shared with each stint at the moment it was added. Loaded
		// in one query for every stint in the answer - a stint carries several windows exactly when the
		// adder's own history had gaps inside the chosen period, and those gaps must stay uncovered here.
		string[] stintIds = [.. stints.Select(m => m.Id)];
		List<SyncMembershipSharedHistoryWindowEntity> sharedWindows = stintIds.Length == 0
			? []
			: await db.SyncMembershipSharedHistoryWindows.AsNoTracking()
				.Where(w => stintIds.Contains(w.MembershipId))
				.ToListAsync(ct);

		// Kind as well as existence: stage 09's personal-correspondence gate applies to a Direct chat and
		// never to a group, and the kind is decided once at creation (it is not re-derived from today's
		// participant count).
		Dictionary<string, SyncChatKind> projectedChats = await db.SyncChats.AsNoTracking()
			.Where(c => chatIds.Contains(c.Id))
			.ToDictionaryAsync(c => c.Id, c => c.Kind, StringComparer.Ordinal, ct);

		// Only needed for chats that have no projection at all - the legacy fallback below.
		int[] unprojected = [.. legacyChatIds.Where(id => !projectedChats.ContainsKey(SyncChatIdentity.IdOf(id)))];
		HashSet<int> legacyMemberships = unprojected.Length == 0
			? []
			: [.. await db.Chats.AsNoTracking()
				.Where(c => unprojected.Contains(c.Id) && c.Users.Any(u => u.Id == legacyUserId))
				.Select(c => c.Id)
				.ToListAsync(ct)];

		// Stage 09: the counterpart of every PERSONAL chat in this answer, and whether new correspondence
		// with them is suspended right now. Resolved once for the whole set (a chat-list surface asks for
		// every chat at once); empty - and not even queried - when the set contains no personal chat.
		IReadOnlyDictionary<int, bool> personalSuspension =
			await PersonalSuspensionAsync(legacyUserId, legacyChatIds, projectedChats, legacyMemberships, ct);

		Dictionary<int, ChatAccess> result = [];
		foreach (int legacyChatId in legacyChatIds)
		{
			string chatId = SyncChatIdentity.IdOf(legacyChatId);
			bool suspended = personalSuspension.TryGetValue(legacyChatId, out bool s) && s;

			if (!projectedChats.ContainsKey(chatId))
			{
				result[legacyChatId] = legacyMemberships.Contains(legacyChatId)
					? ChatAccess.UnboundedActive with { PersonalCorrespondenceSuspended = suspended }
					: ChatAccess.Denied;
				continue;
			}

			SyncMembershipEntity[] own = [.. stints.Where(m => string.Equals(m.ChatId, chatId, StringComparison.Ordinal))];
			if (own.Length == 0)
			{
				result[legacyChatId] = ChatAccess.Denied;
				continue;
			}

			result[legacyChatId] = BuildAccess(own, sharedWindows) with { PersonalCorrespondenceSuspended = suspended };
		}

		return result;
	}

	/// <summary>
	/// One account's stints in ONE chat turned into the union of permitted windows: each stint's
	/// participation window plus whatever older history was shared with it when it was added. A rejoin
	/// therefore keeps the old frozen archive AND the newly shared period AND everything from the rejoin
	/// onwards, with any gap between them covered by no window at all - "Междинните периоди се показват
	/// само ако изрично попадат в новото разрешение."
	/// </summary>
	private static ChatAccess BuildAccess(
		IReadOnlyList<SyncMembershipEntity> own, IReadOnlyList<SyncMembershipSharedHistoryWindowEntity> sharedWindows)
	{
		List<LegacyHistoryWindow> windows = [];
		foreach (SyncMembershipEntity stint in own.OrderBy(m => m.CreatedAtUtc))
		{
			bool frozen = stint.State != SyncMembershipState.Active;
			windows.Add(new LegacyHistoryWindow(
				stint.LegacyHistoryFromUtc, frozen ? stint.EndedAtUtc : null, stint.Id, frozen));

			foreach (SyncMembershipSharedHistoryWindowEntity shared in sharedWindows
				.Where(w => string.Equals(w.MembershipId, stint.Id, StringComparison.Ordinal))
				.OrderBy(w => w.FromUtc))
			{
				// A shared window belongs to the stint it was granted to, so it is frozen exactly when
				// that stint is: a member removed again keeps the period that was shared with it, as an
				// archive, and the status resolution for it follows that stint's own snapshot.
				windows.Add(new LegacyHistoryWindow(shared.FromUtc, shared.ThroughUtc, stint.Id, frozen));
			}
		}

		ChatAccessState state = own.Any(m => m.State == SyncMembershipState.Active)
			? ChatAccessState.Active
			: ChatAccessState.Frozen;

		return new ChatAccess(state, windows);
	}

	/// <summary>
	/// Per chat: is this a personal chat whose new correspondence is suspended for this caller
	/// (plan/09-contacts-privacy.md)? Only a <see cref="SyncChatKind.Direct"/> chat is considered - and,
	/// for a chat created before stage 07 and therefore carrying no kind, a legacy chat with exactly two
	/// participants, which is the same rule its creation applied. A group is never gated here.
	///
	/// The counterpart is read from the LEGACY participant list on purpose: a personal chat's two people
	/// never change, and that row survives everything (it is also what keeps a removed member's archive
	/// readable), so it answers for a chat with or without a membership projection.
	/// </summary>
	private async Task<IReadOnlyDictionary<int, bool>> PersonalSuspensionAsync(
		int legacyUserId, IReadOnlyCollection<int> legacyChatIds, IReadOnlyDictionary<string, SyncChatKind> projectedChats,
		IReadOnlySet<int> legacyMemberships, CancellationToken ct)
	{
		int[] candidates = [.. legacyChatIds.Where(id =>
			projectedChats.TryGetValue(SyncChatIdentity.IdOf(id), out SyncChatKind kind)
				? kind == SyncChatKind.Direct
				: legacyMemberships.Contains(id))];

		if (candidates.Length == 0)
		{
			return new Dictionary<int, bool>();
		}

		var participants = await db.Chats.AsNoTracking()
			.Where(c => candidates.Contains(c.Id))
			.Select(c => new { c.Id, UserIds = c.Users.Select(u => u.Id).ToList() })
			.ToListAsync(ct);

		Dictionary<int, int> counterpartOf = [];
		foreach (var chat in participants)
		{
			// An unprojected chat qualifies only at exactly two participants; a projected Direct chat is
			// personal by its own persisted kind, whatever the legacy row happens to list.
			bool personal = projectedChats.ContainsKey(SyncChatIdentity.IdOf(chat.Id)) || chat.UserIds.Count == 2;
			if (!personal)
			{
				continue;
			}

			int[] others = [.. chat.UserIds.Where(id => id != legacyUserId)];
			if (others.Length == 1)
			{
				counterpartOf[chat.Id] = others[0];
			}
		}

		if (counterpartOf.Count == 0)
		{
			return new Dictionary<int, bool>();
		}

		IReadOnlySet<int> suspended = await relations.SuspendedCorrespondentsAsync(
			legacyUserId, [.. counterpartOf.Values.Distinct()], ct);

		return counterpartOf.ToDictionary(entry => entry.Key, entry => suspended.Contains(entry.Value));
	}
}
