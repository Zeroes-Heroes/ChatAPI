namespace Database.Entities.Sync;

/// <summary>
/// Unread tracking for one membership stint. Keyed 1:1 by MembershipId (not account+chat), so a
/// rejoin naturally starts a fresh unread row on the new stint while the old stint's row stops
/// being written to and stands as its own frozen value - no separate freeze step is needed for unread.
/// ChatId is denormalized from Membership (same pattern as every other message-pointer table in
/// this model) so OldestUnreadMessageId/LatestUnreadMessageId and the membership itself can be
/// pinned to this row's chat via composite FKs, instead of being able to reference a message or
/// membership from any chat in the database.
/// </summary>
public class SyncUnreadStateEntity
{
	public SyncUnreadStateEntity(string membershipId, string chatId, long version, long count)
	{
		MembershipId = membershipId;
		ChatId = chatId;
		Version = version;
		Count = count;
	}

	private SyncUnreadStateEntity() { }

	public string MembershipId { get; set; } = null!;

	/// <summary>Denormalized from Membership for the composite FKs below.</summary>
	public string ChatId { get; set; } = null!;

	public long Version { get; set; }

	public long Count { get; set; }

	public string? OldestUnreadMessageId { get; set; }

	public string? LatestUnreadMessageId { get; set; }

	public DateTime? LatestUnreadCreatedAtUtc { get; set; }

	public virtual SyncMembershipEntity Membership { get; set; } = null!;
	public virtual SyncMessageEntity? OldestUnreadMessage { get; set; }
	public virtual SyncMessageEntity? LatestUnreadMessage { get; set; }
}
