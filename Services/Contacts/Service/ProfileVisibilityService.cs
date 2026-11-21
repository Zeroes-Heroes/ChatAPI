using Database.Context;
using Database.Enums;
using Microsoft.EntityFrameworkCore;
using Services.Contacts.Interface;
using Services.Contacts.Models;

namespace Services.Contacts.Service;

/// <summary>See <see cref="IProfileVisibility"/> for the rule; this is the only implementation of it.</summary>
public sealed class ProfileVisibilityService(AppDbContext db, IPersonalRelationDirectory relations) : IProfileVisibility
{
	public async Task<IReadOnlyDictionary<int, ProfileIdentity>> ResolveAsync(
		int viewerLegacyUserId, IReadOnlyCollection<int> subjectLegacyUserIds, CancellationToken ct = default)
	{
		Dictionary<int, ProfileIdentity> result = [];
		int[] subjects = [.. subjectLegacyUserIds.Distinct()];
		if (subjects.Length == 0)
		{
			return result;
		}

		// Fail closed first: every id asked about has an answer, and the answer is Unknown unless a rule
		// below upgrades it. An id that is not a real user keeps this answer.
		foreach (int id in subjects)
		{
			result[id] = ProfileIdentity.Unknown(id);
		}

		var people = await db.Users.AsNoTracking()
			.Where(u => subjects.Contains(u.Id))
			.Select(u => new { u.Id, u.Name, u.Phone })
			.ToListAsync(ct);

		// A relation row in either direction, with the status that decides. Blocked is read as "no
		// disclosing relation": the block overlay below is what actually governs those pairs, and a legacy
		// Blocked row is exactly such a pair.
		var related = await db.Friendships.AsNoTracking()
			.Where(f => (f.SenderUserId == viewerLegacyUserId && subjects.Contains(f.TargetUserId))
				|| (f.TargetUserId == viewerLegacyUserId && subjects.Contains(f.SenderUserId)))
			.Select(f => new
			{
				OtherId = f.SenderUserId == viewerLegacyUserId ? f.TargetUserId : f.SenderUserId,
				f.Status,
			})
			.ToListAsync(ct);

		HashSet<int> disclosing = [.. related
			.Where(r => r.Status is FriendshipStatus.Accepted or FriendshipStatus.Pending)
			.Select(r => r.OtherId)];

		IReadOnlyDictionary<int, PersonalBlockPair> blocks = await relations.BlocksBetweenAsync(viewerLegacyUserId, subjects, ct);

		foreach (var person in people)
		{
			PersonalBlockPair pair = blocks.TryGetValue(person.Id, out PersonalBlockPair p) ? p : PersonalBlockPair.None;

			bool known =
				person.Id == viewerLegacyUserId
				// The viewer's own block list: without this a viewer could never see whom it blocked, and
				// so could never lift the block.
				|| pair.ViewerBlockedSubject
				|| (disclosing.Contains(person.Id) && !pair.SubjectBlockedViewer);

			if (known)
			{
				result[person.Id] = ProfileIdentity.Known(person.Id, person.Name, person.Phone);
			}
		}

		return result;
	}

	public async Task<IReadOnlySet<int>> ViewersWhoKnowAsync(
		int subjectLegacyUserId, IReadOnlyCollection<int> viewerLegacyUserIds, CancellationToken ct = default)
	{
		int[] viewers = [.. viewerLegacyUserIds.Distinct()];
		HashSet<int> known = [];
		if (viewers.Length == 0)
		{
			return known;
		}

		if (viewers.Contains(subjectLegacyUserId))
		{
			known.Add(subjectLegacyUserId);
		}

		var related = await db.Friendships.AsNoTracking()
			.Where(f => (f.SenderUserId == subjectLegacyUserId && viewers.Contains(f.TargetUserId))
				|| (f.TargetUserId == subjectLegacyUserId && viewers.Contains(f.SenderUserId)))
			.Select(f => new
			{
				ViewerId = f.SenderUserId == subjectLegacyUserId ? f.TargetUserId : f.SenderUserId,
				f.Status,
			})
			.ToListAsync(ct);

		HashSet<int> disclosing = [.. related
			.Where(r => r.Status is FriendshipStatus.Accepted or FriendshipStatus.Pending)
			.Select(r => r.ViewerId)];

		// Asked from the SUBJECT's side, so the pair is read the other way round: "the subject blocked
		// this viewer" is what hides it, and "this viewer blocked the subject" is the own-block-list rule.
		IReadOnlyDictionary<int, PersonalBlockPair> blocks = await relations.BlocksBetweenAsync(subjectLegacyUserId, viewers, ct);

		foreach (int viewer in viewers)
		{
			if (viewer == subjectLegacyUserId)
			{
				continue;
			}

			PersonalBlockPair pair = blocks.TryGetValue(viewer, out PersonalBlockPair p) ? p : PersonalBlockPair.None;
			bool viewerBlockedSubject = pair.SubjectBlockedViewer;
			bool subjectBlockedViewer = pair.ViewerBlockedSubject;

			if (viewerBlockedSubject || (disclosing.Contains(viewer) && !subjectBlockedViewer))
			{
				known.Add(viewer);
			}
		}

		return known;
	}

	public async Task<ProfileIdentity> ResolveAsync(int viewerLegacyUserId, int subjectLegacyUserId, CancellationToken ct = default)
	{
		IReadOnlyDictionary<int, ProfileIdentity> map = await ResolveAsync(viewerLegacyUserId, [subjectLegacyUserId], ct);
		return map.TryGetValue(subjectLegacyUserId, out ProfileIdentity? identity) ? identity : ProfileIdentity.Unknown(subjectLegacyUserId);
	}
}
