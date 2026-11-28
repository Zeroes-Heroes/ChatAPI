using Services.Contacts.Models;

namespace Services.Contacts.Interface;

/// <summary>
/// The single reader of the personal-relation model (plan/09-contacts-privacy.md). Every rule that
/// depends on "what is between these two people" asks THIS - the membership writer's add rule through
/// <see cref="Services.Membership.Interface.IPersonalBlockDirectory"/>, the correspondence gate inside
/// <see cref="Services.Membership.Service.ChatAccessAuthority"/>, and the profile resolver
/// <see cref="IProfileVisibility"/>.
///
/// Two sources are unioned, deliberately:
/// <list type="number">
/// <item>the stage-09 <c>SyncPersonalBlocks</c> rows (directional, explicit blocker, liftable);</item>
/// <item>legacy <c>Friendships</c> rows that already reached
/// <see cref="Database.Enums.FriendshipStatus.Blocked"/> before this stage. That status could only be
/// set by the row's <c>TargetUserId</c> (<c>POST /friends/request-respond</c> passes the caller as the
/// target), so on such a row the blocker is <c>TargetUserId</c> - exactly the derivation stage 07 used.
/// Unioning them is what makes this stage need no backfill, and it is also why lifting such a legacy
/// block has to clear the legacy status as well as the new row.</item>
/// </list>
/// </summary>
public interface IPersonalRelationDirectory
{
	/// <summary>Blocks in force between <paramref name="viewerLegacyUserId"/> and each candidate, in both directions.</summary>
	Task<IReadOnlyDictionary<int, PersonalBlockPair>> BlocksBetweenAsync(
		int viewerLegacyUserId, IReadOnlyCollection<int> candidateLegacyUserIds, CancellationToken ct = default);

	/// <summary>Same answer for a single pair; <see cref="PersonalBlockPair.None"/> for the caller itself.</summary>
	Task<PersonalBlockPair> BlocksBetweenAsync(int viewerLegacyUserId, int subjectLegacyUserId, CancellationToken ct = default);

	/// <summary>
	/// Every person this viewer blocks or is blocked by, with no candidate set supplied. The contact list
	/// needs exactly this: a block can exist with NO friendship row behind it (blocking someone met in a
	/// group), and such a pair still has to appear in the viewer's own blocked list - otherwise the block
	/// would be invisible and could never be lifted.
	/// </summary>
	Task<IReadOnlyDictionary<int, PersonalBlockPair>> AllBlocksAsync(int viewerLegacyUserId, CancellationToken ct = default);

	/// <summary>
	/// Of <paramref name="candidateLegacyUserIds"/>, the ones that may NOT exchange new personal
	/// correspondence with <paramref name="viewerLegacyUserId"/>: someone either side has blocked, or
	/// someone who is not a current (accepted) contact at all - "Блокиране или премахване на контакт
	/// прекратява новата лична кореспонденция". Answered for the whole set in two queries, because the
	/// chat-list surface asks it for every personal chat at once.
	///
	/// This is about PERSONAL chats only. A group is never gated by it ("Личното блокиране запазва
	/// активното участие и кореспонденцията в общи групи"), which is why the chat's own kind - not this
	/// answer - decides whether to consult it at all.
	/// </summary>
	Task<IReadOnlySet<int>> SuspendedCorrespondentsAsync(
		int viewerLegacyUserId, IReadOnlyCollection<int> candidateLegacyUserIds, CancellationToken ct = default);
}
