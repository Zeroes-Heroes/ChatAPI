namespace Services.Membership.Interface;

/// <summary>
/// Who has blocked whom, for the one membership rule that depends on it (plan/07-membership.md:
/// "Добавянето ... следва ограниченията от блокиране"; MESSAGE_SYNC_ANALYSIS.md: "Ако А е блокирал Б,
/// Б не може да добавя А; друг участник, когото А не е блокирал, може да го добави").
///
/// Direction comes from the legacy <see cref="Database.Entities.FriendshipEntity"/>, which stores no
/// explicit blocker column: a row reaches <see cref="Database.Enums.FriendshipStatus.Blocked"/> only
/// through <c>POST /friends/request-respond</c>, whose caller is always the row's
/// <c>TargetUserId</c> (the party that received the request - see
/// <c>FriendsController.RespondToFriendRequest</c>, which passes <c>User.Id()</c> as the target and the
/// other party as the sender). So on a Blocked row the blocker is <c>TargetUserId</c> and the blocked
/// party is <c>SenderUserId</c>. The client agrees: its blocked-contacts list reads exactly the
/// Blocked relations where the current user is NOT the initiator.
///
/// Stage 09 owns the full personal-block model (and may record the blocker explicitly); this interface
/// is the single place that would change if it does, instead of the rule being inlined into the
/// membership writer.
/// </summary>
public interface IPersonalBlockDirectory
{
	/// <summary>
	/// The subset of <paramref name="candidateLegacyUserIds"/> that have blocked
	/// <paramref name="actorLegacyUserId"/>. Deliberately one-directional: a block must stop the BLOCKED
	/// party from adding the blocker, while leaving every other participant free to add them, so the
	/// answer may not be symmetric.
	/// </summary>
	Task<IReadOnlySet<int>> WhoBlockedAsync(int actorLegacyUserId, IReadOnlyCollection<int> candidateLegacyUserIds, CancellationToken ct = default);
}
