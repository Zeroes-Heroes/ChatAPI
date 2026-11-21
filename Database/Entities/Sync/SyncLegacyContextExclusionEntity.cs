namespace Database.Entities.Sync;

/// <summary>
/// The fresh-start boundary, recorded as data rather than inferred: one row per legacy chat that existed
/// for this phone at the moment a NEW account context superseded the old one (plan/06-recovery.md: "ново
/// начало ... без стара история"). The legacy chat/message tables have no account context and no creation
/// timestamp, so this snapshot is what lets the still-legacy read/publish surfaces
/// (<see cref="Services.Chat.Interface.IChatService"/>, <c>BaseHub</c>) exclude the old context's chats
/// for the new context while leaving every row in place - nothing is deleted or reassigned.
/// </summary>
public class SyncLegacyContextExclusionEntity
{
	public SyncLegacyContextExclusionEntity(string accountId, int legacyChatId, DateTime createdAtUtc)
	{
		AccountId = accountId;
		LegacyChatId = legacyChatId;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncLegacyContextExclusionEntity() { }

	/// <summary>The NEW context the exclusion applies to.</summary>
	public string AccountId { get; set; } = null!;

	public int LegacyChatId { get; set; }

	public DateTime CreatedAtUtc { get; set; }

	public virtual SyncAccountEntity Account { get; set; } = null!;
}
