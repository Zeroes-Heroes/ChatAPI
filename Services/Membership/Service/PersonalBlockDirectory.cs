using Services.Contacts.Interface;
using Services.Contacts.Models;
using Services.Membership.Interface;

namespace Services.Membership.Service;

/// <summary>
/// See <see cref="IPersonalBlockDirectory"/> for the direction this answers.
///
/// Stage 09 moved the model itself into <c>Services/Contacts</c>
/// (<see cref="IPersonalRelationDirectory"/>), exactly where stage 07 said it would go: the blocker is
/// now explicit on <c>SyncPersonalBlocks</c> instead of being derived from which column of a legacy
/// <c>Friendships</c> row happens to hold it, and a block can exist with no friendship row at all. This
/// stays as the membership module's own narrow question so its call sites keep asking for exactly the
/// one direction they need.
/// </summary>
public sealed class PersonalBlockDirectory(IPersonalRelationDirectory relations) : IPersonalBlockDirectory
{
	public async Task<IReadOnlySet<int>> WhoBlockedAsync(
		int actorLegacyUserId, IReadOnlyCollection<int> candidateLegacyUserIds, CancellationToken ct = default)
	{
		int[] candidates = [.. candidateLegacyUserIds.Where(id => id != actorLegacyUserId)];
		if (candidates.Length == 0)
		{
			return new HashSet<int>();
		}

		IReadOnlyDictionary<int, PersonalBlockPair> blocks = await relations.BlocksBetweenAsync(actorLegacyUserId, candidates, ct);

		// Only "they blocked me": a block must stop the BLOCKED party from adding its blocker while
		// leaving every other participant free to add them, so the answer is deliberately asymmetric.
		return blocks.Where(entry => entry.Value.SubjectBlockedViewer).Select(entry => entry.Key).ToHashSet();
	}
}
