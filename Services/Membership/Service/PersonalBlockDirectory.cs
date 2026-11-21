using Database.Context;
using Database.Enums;
using Microsoft.EntityFrameworkCore;
using Services.Membership.Interface;

namespace Services.Membership.Service;

/// <summary>See <see cref="IPersonalBlockDirectory"/> for the direction argument this query encodes.</summary>
public sealed class PersonalBlockDirectory(AppDbContext db) : IPersonalBlockDirectory
{
	public async Task<IReadOnlySet<int>> WhoBlockedAsync(
		int actorLegacyUserId, IReadOnlyCollection<int> candidateLegacyUserIds, CancellationToken ct = default)
	{
		int[] candidates = [.. candidateLegacyUserIds.Where(id => id != actorLegacyUserId)];
		if (candidates.Length == 0)
		{
			return new HashSet<int>();
		}

		// Blocker = TargetUserId (the only party the legacy API lets set Blocked), blocked = SenderUserId.
		List<int> blockers = await db.Friendships.AsNoTracking()
			.Where(f => f.Status == FriendshipStatus.Blocked
				&& f.SenderUserId == actorLegacyUserId
				&& candidates.Contains(f.TargetUserId))
			.Select(f => f.TargetUserId)
			.Distinct()
			.ToListAsync(ct);

		return blockers.ToHashSet();
	}
}
