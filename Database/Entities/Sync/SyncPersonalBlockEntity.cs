namespace Database.Entities.Sync;

/// <summary>
/// One <b>directional</b> personal block: <see cref="BlockerUserId"/> has blocked
/// <see cref="SubjectUserId"/> (plan/09-contacts-privacy.md). The legacy
/// <see cref="FriendshipEntity"/> has no blocker column - a row only reaches
/// <see cref="Database.Enums.FriendshipStatus.Blocked"/> through <c>POST /friends/request-respond</c>,
/// whose caller is always the row's <c>TargetUserId</c> - so it can express neither "the request's
/// SENDER blocked the other party" nor a mutual block. This table does both, and is what makes the
/// blocker explicit instead of derived.
///
/// Keyed by <b>legacy user ids</b>, like the friendship it overlays, so a fresh start (plan/06: a new
/// AccountContextId on the same phone and the same Users row) keeps a block exactly as it keeps a
/// friendship. A person who blocked someone does not get a clean slate by starting over, and neither
/// does the blocked party.
///
/// One row per ordered pair, ever: a re-block reuses the row (<see cref="LiftedAtUtc"/> back to null).
/// That is what makes block/unblock converge under a lost response and a retry - the second attempt
/// writes the same state rather than inserting a second, differently-dated record.
///
/// The legacy <c>Friendships.Status</c> is deliberately NOT changed by a block: it keeps meaning the
/// friend-request lifecycle, so lifting the block restores the underlying relation
/// ("Отблокирането възстановява достъпа за новата кореспонденция") with no "previous status" to store.
/// </summary>
public class SyncPersonalBlockEntity
{
	public SyncPersonalBlockEntity(int blockerUserId, int subjectUserId, DateTime createdAtUtc)
	{
		BlockerUserId = blockerUserId;
		SubjectUserId = subjectUserId;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncPersonalBlockEntity() { }

	/// <summary>The person who blocked. Part of the primary key together with <see cref="SubjectUserId"/>.</summary>
	public int BlockerUserId { get; set; }

	/// <summary>The person who is blocked.</summary>
	public int SubjectUserId { get; set; }

	/// <summary>When the block currently in force (or the last one) was established.</summary>
	public DateTime CreatedAtUtc { get; set; }

	/// <summary>Null while the block is in force. Set when it is lifted; a later re-block clears it again.</summary>
	public DateTime? LiftedAtUtc { get; set; }

	public virtual UserEntity Blocker { get; set; } = null!;
	public virtual UserEntity Subject { get; set; } = null!;

	public bool IsActive => LiftedAtUtc is null;
}
