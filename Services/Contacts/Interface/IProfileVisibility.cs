using Services.Contacts.Models;

namespace Services.Contacts.Interface;

/// <summary>
/// The one resolver of "what may this viewer learn about this person" (plan/09-contacts-privacy.md:
/// "Неприятелите се представят като unknown във всички изходи, включително история, събития и
/// известия. Телефон, снимка и лични данни следват отношенията между хората").
///
/// <b>The rule, in one place.</b> A subject is KNOWN to a viewer when
/// <list type="number">
/// <item>it is the viewer itself; or</item>
/// <item>the viewer actively blocks the subject - a viewer must always be able to read and manage its
/// OWN block list (<c>GET /friends?status=Blocked</c> is how a block is lifted at all); or</item>
/// <item>a <see cref="Database.Entities.FriendshipEntity"/> exists between them whose status is
/// <see cref="Database.Enums.FriendshipStatus.Pending"/> or
/// <see cref="Database.Enums.FriendshipStatus.Accepted"/>, AND the subject does not actively block the
/// viewer.</item>
/// </list>
/// Everyone else is <see cref="ProfileIdentity.Unknown"/>: a stranger who merely shares a group
/// ("Участниците в група могат да са непознати помежду си"), a removed contact, a rejected request and
/// a party that has been blocked. Pending is known on purpose - a friend request is an explicit
/// disclosure by the person who typed that phone number, and its recipient needs the identity in order
/// to answer it at all.
///
/// It fails CLOSED: an id with no resolvable relation, and an id that is not a real user, both answer
/// Unknown rather than being skipped.
/// </summary>
public interface IProfileVisibility
{
	/// <summary>One round trip for a whole audience (a chat's participants, a push fan-out, a contact list).</summary>
	Task<IReadOnlyDictionary<int, ProfileIdentity>> ResolveAsync(
		int viewerLegacyUserId, IReadOnlyCollection<int> subjectLegacyUserIds, CancellationToken ct = default);

	/// <summary>Single subject; same rule, same fail-closed answer.</summary>
	Task<ProfileIdentity> ResolveAsync(int viewerLegacyUserId, int subjectLegacyUserId, CancellationToken ct = default);

	/// <summary>
	/// The transposed question: of these VIEWERS, which ones may see <paramref name="subjectLegacyUserId"/>?
	/// Same rule, evaluated once for the whole audience. The push fan-out needs exactly this - one message
	/// to many recipients, each of whom may or may not have a disclosing relation with the one sender -
	/// and asking <see cref="ResolveAsync(int, int, CancellationToken)"/> per recipient would be one pair
	/// of queries per device.
	/// </summary>
	Task<IReadOnlySet<int>> ViewersWhoKnowAsync(
		int subjectLegacyUserId, IReadOnlyCollection<int> viewerLegacyUserIds, CancellationToken ct = default);
}
