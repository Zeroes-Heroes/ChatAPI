using System.Security.Claims;
using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
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
public sealed class ChatAccessAuthority(AppDbContext db, IAccountContextBoundary boundary) : IChatAccessAuthority
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

		HashSet<string> projectedChats = [.. await db.SyncChats.AsNoTracking()
			.Where(c => chatIds.Contains(c.Id))
			.Select(c => c.Id)
			.ToListAsync(ct)];

		// Only needed for chats that have no projection at all - the legacy fallback below.
		int[] unprojected = [.. legacyChatIds.Where(id => !projectedChats.Contains(SyncChatIdentity.IdOf(id)))];
		HashSet<int> legacyMemberships = unprojected.Length == 0
			? []
			: [.. await db.Chats.AsNoTracking()
				.Where(c => unprojected.Contains(c.Id) && c.Users.Any(u => u.Id == legacyUserId))
				.Select(c => c.Id)
				.ToListAsync(ct)];

		Dictionary<int, ChatAccess> result = [];
		foreach (int legacyChatId in legacyChatIds)
		{
			string chatId = SyncChatIdentity.IdOf(legacyChatId);
			if (!projectedChats.Contains(chatId))
			{
				result[legacyChatId] = legacyMemberships.Contains(legacyChatId) ? ChatAccess.UnboundedActive : ChatAccess.Denied;
				continue;
			}

			SyncMembershipEntity[] own = [.. stints.Where(m => string.Equals(m.ChatId, chatId, StringComparison.Ordinal))];
			if (own.Length == 0)
			{
				result[legacyChatId] = ChatAccess.Denied;
				continue;
			}

			// The union of every stint's windows: its participation window plus whatever older history was
			// shared with it when it was added. A rejoin therefore keeps the old frozen archive AND the
			// newly shared period AND everything from the rejoin onwards, with any gap between them
			// covered by no window at all - "Междинните периоди се показват само ако изрично попадат в
			// новото разрешение."
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

			result[legacyChatId] = new ChatAccess(state, windows);
		}

		return result;
	}
}
