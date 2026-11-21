using Database.Enums.Sync;

namespace Database.Entities.Sync;

/// <summary>
/// A frozen copy of one recipient's status for one message, taken once when a membership stint
/// was removed/left. Exists because the live SyncMessageRecipientState row for that message keeps
/// changing for the chat's still-active members (new Seen events, etc.); without this copy the
/// frozen member's archive view would drift after the fact, and could not be reconstructed at all
/// once the per-chat change journal is pruned. Covers every message within the frozen member's
/// allowed history ranges, for every recipient of that message (sender excluded), so both the
/// group Received/Seen proportions and the author's individual-status view can be rebuilt later.
/// </summary>
public class SyncMembershipMessageStatusSnapshotEntity
{
	public SyncMembershipMessageStatusSnapshotEntity(string membershipId, string chatId, string messageId, string recipientAccountId)
	{
		MembershipId = membershipId;
		ChatId = chatId;
		MessageId = messageId;
		RecipientAccountId = recipientAccountId;
	}

	private SyncMembershipMessageStatusSnapshotEntity() { }

	/// <summary>The frozen membership stint whose archive this status snapshot belongs to.</summary>
	public string MembershipId { get; set; } = null!;

	/// <summary>Denormalized from Membership/Message for the composite FKs; pins both to the same chat.</summary>
	public string ChatId { get; set; } = null!;

	public string MessageId { get; set; } = null!;

	public string RecipientAccountId { get; set; } = null!;

	public DateTime? ReceivedAtUtc { get; set; }

	public DateTime? SeenAtUtc { get; set; }

	public SyncSeenReason? SeenReason { get; set; }

	public virtual SyncMembershipEntity Membership { get; set; } = null!;
	public virtual SyncMessageEntity Message { get; set; } = null!;
	public virtual SyncAccountEntity RecipientAccount { get; set; } = null!;
}
