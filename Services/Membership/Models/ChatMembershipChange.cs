namespace Services.Membership.Models;

/// <summary>
/// What one committed membership mutation did, in the legacy ids every reachable client surface
/// speaks. Returned to the caller (so its own UI applies the change without waiting for the live
/// event) and broadcast to the chat (<see cref="Services.Utilities.Statics.LiveEvents.ChatMembershipChanged"/>).
///
/// <paramref name="IsArchived"/> is always from the point of view of whoever receives this exact copy:
/// the caller's own HTTP response, the remaining participants' copy, the departing participant's copy
/// and the removed subject's copy are distinct values of this record, not one payload reinterpreted per
/// recipient (see ChatMembershipService.BroadcastAsync).
///
/// <paramref name="ActorUserId"/> is null for the copy delivered to a REMOVED subject:
/// docs/sync-contract-v1.md requires "ActorAccountContextId ... always null for a Removed event
/// delivered to the removed subject", and MESSAGE_SYNC_ANALYSIS.md repeats it ("Премахнатият вижда, че
/// е премахнат, без самоличността на премахналия го"). A voluntary departure is the subject's own
/// action, so their own copy keeps the actor and stays distinguishable from a removal.
/// </summary>
public sealed record ChatMembershipChange(
	int ChatId,
	bool IsGroup,
	bool IsArchived,
	IReadOnlyList<int> AddedUserIds,
	IReadOnlyList<int> RemovedUserIds,
	IReadOnlyList<int> LeftUserIds,
	IReadOnlyList<int> ActiveUserIds,
	int? ActorUserId);
