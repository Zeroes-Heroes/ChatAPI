using System.Security.Claims;
using Database.Context;
using Microsoft.EntityFrameworkCore;
using Services.Sessions.Interface;

namespace Services.Sessions.Service;

/// <summary>
/// See <see cref="IAccountContextBoundary"/>. Reads the exclusion snapshot recorded when the context was
/// created (<see cref="Database.Entities.Sync.SyncLegacyContextExclusionEntity"/>) - never a timestamp
/// comparison, because legacy chats have no creation time - and is scoped per request: the caller's own
/// AccountContextId claim decides, never a user id.
/// </summary>
public sealed class AccountContextBoundary(AppDbContext db, ISessionConnectionRegistry connections) : IAccountContextBoundary
{
	public async Task<IReadOnlySet<int>> ExcludedLegacyChatIdsAsync(ClaimsPrincipal caller, CancellationToken ct = default)
	{
		string? accountId = caller.FindFirstValue(SessionClaimTypes.AccountContextId);
		if (string.IsNullOrEmpty(accountId))
		{
			return new HashSet<int>();
		}

		List<int> excluded = await db.SyncLegacyContextExclusions.AsNoTracking()
			.Where(e => e.AccountId == accountId)
			.Select(e => e.LegacyChatId)
			.ToListAsync(ct);

		return excluded.ToHashSet();
	}

	public async Task<bool> IsLegacyChatExcludedAsync(ClaimsPrincipal caller, int legacyChatId, CancellationToken ct = default)
	{
		string? accountId = caller.FindFirstValue(SessionClaimTypes.AccountContextId);
		if (string.IsNullOrEmpty(accountId))
		{
			return false;
		}

		return await db.SyncLegacyContextExclusions.AsNoTracking()
			.AnyAsync(e => e.AccountId == accountId && e.LegacyChatId == legacyChatId, ct);
	}

	public async Task<LegacyRecipients> ResolveLegacyRecipientsAsync(
		IReadOnlyCollection<int> legacyUserIds, int legacyChatId, CancellationToken ct = default)
	{
		int[] recipients = [.. legacyUserIds];
		if (recipients.Length == 0)
		{
			return new LegacyRecipients([], []);
		}

		// Which account contexts of these recipients must not see this chat at all.
		var excluded = await db.SyncLegacyContextExclusions.AsNoTracking()
			.Where(e => e.LegacyChatId == legacyChatId && recipients.Contains(e.Account.LegacyUserId))
			.Select(e => new { e.AccountId, e.Account.LegacyUserId })
			.ToListAsync(ct);
		if (excluded.Count == 0)
		{
			// The overwhelmingly common case: nobody on this chat has ever started over, so delivery is
			// exactly what it was before stage 06.
			return new LegacyRecipients(recipients, []);
		}

		HashSet<string> excludedAccountIds = excluded.Select(e => e.AccountId).ToHashSet(StringComparer.Ordinal);
		HashSet<int> affectedUsers = excluded.Select(e => e.LegacyUserId).ToHashSet();

		// For an affected phone, user-id fan-out cannot distinguish its contexts, so delivery switches to
		// that phone's individual connections. Only connections registered on THIS replica are known
		// (ISessionConnectionRegistry is per-process, plan 21 territory) - a connection parked on another
		// replica therefore misses this live event rather than receiving the old context's content, which
		// is the fail-closed side of that limitation.
		string[] candidateAccountIds = [.. connections.Snapshot().Select(c => c.AccountId).Distinct(StringComparer.Ordinal)];
		var localAccounts = await db.SyncAccounts.AsNoTracking()
			.Where(a => candidateAccountIds.Contains(a.Id) && affectedUsers.Contains(a.LegacyUserId))
			.Select(a => new { a.Id, a.LegacyUserId })
			.ToListAsync(ct);
		HashSet<string> allowedAccountIds = localAccounts
			.Where(a => !excludedAccountIds.Contains(a.Id))
			.Select(a => a.Id)
			.ToHashSet(StringComparer.Ordinal);

		string[] connectionIds = [.. connections.Snapshot()
			.Where(c => allowedAccountIds.Contains(c.AccountId))
			.Select(c => c.ConnectionId)];

		return new LegacyRecipients([.. recipients.Where(id => !affectedUsers.Contains(id))], connectionIds);
	}

	public async Task<IReadOnlyList<int>> NotifiableLegacyUserIdsAsync(
		IReadOnlyCollection<int> legacyUserIds, int legacyChatId, CancellationToken ct = default)
	{
		int[] recipients = [.. legacyUserIds];
		if (recipients.Length == 0)
		{
			return [];
		}

		// Only the phone's CURRENT context can be addressed (a push has no connection to target), and an
		// ended context has no live session left either way - so a phone whose current context excludes
		// this chat is dropped entirely rather than being sent the old context's message text.
		List<int> blocked = await db.SyncLegacyContextExclusions.AsNoTracking()
			.Where(e => e.LegacyChatId == legacyChatId
				&& e.Account.EndedAtUtc == null
				&& recipients.Contains(e.Account.LegacyUserId))
			.Select(e => e.Account.LegacyUserId)
			.Distinct()
			.ToListAsync(ct);
		if (blocked.Count == 0)
		{
			// The overwhelmingly common case: nobody on this chat has ever started over.
			return recipients;
		}

		HashSet<int> excludedUsers = [.. blocked];
		return [.. recipients.Where(id => !excludedUsers.Contains(id))];
	}
}
