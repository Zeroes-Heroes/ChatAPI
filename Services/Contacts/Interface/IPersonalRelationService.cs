using System.Security.Claims;
using Services.Contacts.Models;
using Services.Utilities;

namespace Services.Contacts.Interface;

/// <summary>
/// The single WRITER of the personal-relation model (plan/09-contacts-privacy.md). Every mutation here
/// runs inside <see cref="Services.Sessions.Service.TrustedMutation"/> - the caller's own
/// session/device/trust-version is re-validated, locked, immediately before the row commits - and
/// broadcasts <c>personal-relation-changed</c> to BOTH parties strictly after a successful commit, so a
/// rolled-back attempt publishes nothing and a device learns over SignalR exactly what
/// <c>GET /friends</c> would tell it over HTTP.
///
/// All three operations are idempotent, because a lost response followed by a retry is the normal case:
/// the block row's primary key is the ordered pair, so a second block updates one row instead of
/// writing a second record, and unblocking an already-lifted block (or removing an already-removed
/// contact) answers with the current state and writes nothing.
/// </summary>
public interface IPersonalRelationService
{
	/// <summary>
	/// The caller blocks <paramref name="subjectLegacyUserId"/>: new personal correspondence between them
	/// stops at once, shared groups are untouched, and the subject loses the caller's profile data. The
	/// caller's own view of the subject is kept (it is the caller's own block list).
	/// </summary>
	Task<Result<PersonalRelationChange>> BlockAsync(ClaimsPrincipal actingUser, int subjectLegacyUserId, CancellationToken ct = default);

	/// <summary>
	/// Lifts the caller's own block. "Отблокирането възстановява достъпа за новата лична кореспонденция"
	/// - the underlying friendship status was never overwritten, so it simply applies again. A legacy
	/// <c>Friendships.Status = Blocked</c> row written before this stage is restored to
	/// <see cref="Database.Enums.FriendshipStatus.Accepted"/>, which is the status it replaced.
	/// </summary>
	Task<Result<PersonalRelationChange>> UnblockAsync(ClaimsPrincipal actingUser, int subjectLegacyUserId, CancellationToken ct = default);

	/// <summary>
	/// Removes a contact - available to EITHER party, unlike the legacy status endpoint, which only the
	/// request's target could use. New personal correspondence stops; the permitted history stays
	/// readable and keeps receiving status updates; the contact may be added again later.
	/// </summary>
	Task<Result<PersonalRelationChange>> RemoveContactAsync(ClaimsPrincipal actingUser, int subjectLegacyUserId, CancellationToken ct = default);
}

/// <summary>
/// One committed relation change, in the legacy ids every reachable surface speaks. Returned to the
/// caller (so its own UI applies the change without waiting for the event) and broadcast to both
/// parties - each with its OWN copy, because the two sides do not see the same thing: the blocker keeps
/// the subject's profile, the subject gets <see cref="ProfileIdentity.Unknown"/>.
/// </summary>
/// <param name="FriendshipStatus">The relation status as THIS recipient should display it (the legacy status, with an active block of this recipient's own projected onto it).</param>
/// <param name="IsInitiator">Whether this recipient is the sender of the underlying friend request - the flag the client's own contact lists filter by.</param>
/// <param name="BlockedByMe">This recipient actively blocks the other party.</param>
/// <param name="CorrespondenceSuspended">No new personal correspondence may be created between the two right now (either side's block, or no accepted contact).</param>
public sealed record PersonalRelationChange(
	int UserId,
	ProfileIdentity Profile,
	int FriendshipStatus,
	bool IsInitiator,
	bool BlockedByMe,
	bool CorrespondenceSuspended);
