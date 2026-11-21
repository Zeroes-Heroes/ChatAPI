using Database.Context;
using Database.Enums;
using Microsoft.EntityFrameworkCore;
using Services.Contacts.Interface;
using Services.Contacts.Models;

namespace Services.Contacts.Service;

/// <summary>See <see cref="IPersonalRelationDirectory"/> for the two sources this unions and why.</summary>
public sealed class PersonalRelationDirectory(AppDbContext db) : IPersonalRelationDirectory
{
	public async Task<IReadOnlyDictionary<int, PersonalBlockPair>> BlocksBetweenAsync(
		int viewerLegacyUserId, IReadOnlyCollection<int> candidateLegacyUserIds, CancellationToken ct = default)
	{
		Dictionary<int, PersonalBlockPair> result = [];
		int[] candidates = [.. candidateLegacyUserIds.Distinct().Where(id => id != viewerLegacyUserId)];

		foreach (int id in candidateLegacyUserIds.Distinct())
		{
			result[id] = PersonalBlockPair.None;
		}

		if (candidates.Length == 0)
		{
			return result;
		}

		HashSet<int> blockedByViewer = [];
		HashSet<int> blockedViewer = [];

		var rows = await db.SyncPersonalBlocks.AsNoTracking()
			.Where(b => b.LiftedAtUtc == null
				&& ((b.BlockerUserId == viewerLegacyUserId && candidates.Contains(b.SubjectUserId))
					|| (b.SubjectUserId == viewerLegacyUserId && candidates.Contains(b.BlockerUserId))))
			.Select(b => new { b.BlockerUserId, b.SubjectUserId })
			.ToListAsync(ct);

		foreach (var row in rows)
		{
			if (row.BlockerUserId == viewerLegacyUserId)
			{
				blockedByViewer.Add(row.SubjectUserId);
			}
			else
			{
				blockedViewer.Add(row.BlockerUserId);
			}
		}

		// Legacy rows that reached Blocked before this stage: the blocker is the row's TargetUserId,
		// because that status could only ever be set by the request's target (see the interface's own doc
		// comment). No backfill is needed precisely because they are read here.
		var legacy = await db.Friendships.AsNoTracking()
			.Where(f => f.Status == FriendshipStatus.Blocked
				&& ((f.SenderUserId == viewerLegacyUserId && candidates.Contains(f.TargetUserId))
					|| (f.TargetUserId == viewerLegacyUserId && candidates.Contains(f.SenderUserId))))
			.Select(f => new { f.SenderUserId, f.TargetUserId })
			.ToListAsync(ct);

		foreach (var row in legacy)
		{
			if (row.TargetUserId == viewerLegacyUserId)
			{
				blockedByViewer.Add(row.SenderUserId);
			}
			else
			{
				blockedViewer.Add(row.TargetUserId);
			}
		}

		foreach (int id in candidates)
		{
			result[id] = new PersonalBlockPair(blockedByViewer.Contains(id), blockedViewer.Contains(id));
		}

		return result;
	}

	public async Task<PersonalBlockPair> BlocksBetweenAsync(int viewerLegacyUserId, int subjectLegacyUserId, CancellationToken ct = default)
	{
		IReadOnlyDictionary<int, PersonalBlockPair> map = await BlocksBetweenAsync(viewerLegacyUserId, [subjectLegacyUserId], ct);
		return map.TryGetValue(subjectLegacyUserId, out PersonalBlockPair pair) ? pair : PersonalBlockPair.None;
	}

	public async Task<IReadOnlyDictionary<int, PersonalBlockPair>> AllBlocksAsync(int viewerLegacyUserId, CancellationToken ct = default)
	{
		List<int> partners = await db.SyncPersonalBlocks.AsNoTracking()
			.Where(b => b.LiftedAtUtc == null && (b.BlockerUserId == viewerLegacyUserId || b.SubjectUserId == viewerLegacyUserId))
			.Select(b => b.BlockerUserId == viewerLegacyUserId ? b.SubjectUserId : b.BlockerUserId)
			.ToListAsync(ct);

		// Same union as everywhere else in this type: a pre-stage-09 legacy Blocked row is a block too.
		partners.AddRange(await db.Friendships.AsNoTracking()
			.Where(f => f.Status == FriendshipStatus.Blocked
				&& (f.SenderUserId == viewerLegacyUserId || f.TargetUserId == viewerLegacyUserId))
			.Select(f => f.SenderUserId == viewerLegacyUserId ? f.TargetUserId : f.SenderUserId)
			.ToListAsync(ct));

		return await BlocksBetweenAsync(viewerLegacyUserId, [.. partners.Distinct()], ct);
	}

	public async Task<IReadOnlySet<int>> SuspendedCorrespondentsAsync(
		int viewerLegacyUserId, IReadOnlyCollection<int> candidateLegacyUserIds, CancellationToken ct = default)
	{
		int[] candidates = [.. candidateLegacyUserIds.Distinct().Where(id => id != viewerLegacyUserId)];
		HashSet<int> suspended = [];
		if (candidates.Length == 0)
		{
			return suspended;
		}

		// "Премахване на контакт прекратява новата лична кореспонденция" - a removed contact is a
		// Friendships row that is no longer Accepted (the legacy representation of a removal, and the one
		// AddFriend already treats as re-addable), so the gate is "currently accepted contact" rather than
		// "no block". A pair with no row at all is likewise suspended; a personal chat can only have been
		// created between accepted friends in the first place.
		HashSet<int> accepted = [.. await db.Friendships.AsNoTracking()
			.Where(f => f.Status == FriendshipStatus.Accepted
				&& ((f.SenderUserId == viewerLegacyUserId && candidates.Contains(f.TargetUserId))
					|| (f.TargetUserId == viewerLegacyUserId && candidates.Contains(f.SenderUserId))))
			.Select(f => f.SenderUserId == viewerLegacyUserId ? f.TargetUserId : f.SenderUserId)
			.ToListAsync(ct)];

		IReadOnlyDictionary<int, PersonalBlockPair> blocks = await BlocksBetweenAsync(viewerLegacyUserId, candidates, ct);

		foreach (int id in candidates)
		{
			bool blocked = blocks.TryGetValue(id, out PersonalBlockPair pair) && pair.AnyBlock;
			if (blocked || !accepted.Contains(id))
			{
				suspended.Add(id);
			}
		}

		return suspended;
	}
}
