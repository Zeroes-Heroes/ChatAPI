using Database.Enums;

namespace Services.Friendship.Models;

/// <summary>
/// One personal relation as THIS caller may see it (stage 09, plan/09-contacts-privacy.md).
/// <paramref name="Name"/>/<paramref name="Phone"/> are already resolved through
/// <see cref="Services.Contacts.Interface.IProfileVisibility"/>, so a party with no disclosing relation
/// reads <c>"unknown"</c> with an empty phone rather than being filtered out of the list - the relation
/// itself still has to be listed and acted on.
/// </summary>
/// <param name="FriendshipStatus">The status to display, with an active block projected over the legacy friend-request status.</param>
/// <param name="IsInitiator">Who started the underlying request; forced to "the blocker is not the initiator" for a blocked pair, which is the convention the blocked-contacts list filters by.</param>
/// <param name="IsKnown">False when the profile fields are the <c>unknown</c> placeholder rather than this person's own data.</param>
/// <param name="BlockedByMe">This caller actively blocks the other party (the caller's own block list).</param>
/// <param name="CorrespondenceSuspended">No NEW personal correspondence may be created with this person right now.</param>
public record FriendshipDTO(
	int UserId,
	string Name,
	string Phone,
	int FriendshipStatus,
	bool IsInitiator,
	bool IsKnown = true,
	bool BlockedByMe = false,
	bool CorrespondenceSuspended = false);

/// <summary>
/// One raw legacy <see cref="Database.Entities.FriendshipEntity"/> row from this caller's point of
/// view, before stage 09's block overlay and profile resolution are applied. Deliberately NOT the DTO:
/// the overlay can change both the status and <see cref="IsInitiator"/>, so the caller's status/role
/// filters have to be applied AFTER it, not in the SQL that reads these rows.
/// </summary>
public record FriendshipRelationRow(int UserId, string Name, string Phone, FriendshipStatus Status, bool IsInitiator);
