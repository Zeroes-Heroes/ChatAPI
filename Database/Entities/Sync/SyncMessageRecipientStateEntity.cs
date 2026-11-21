using Database.Enums.Sync;

namespace Database.Entities.Sync;

/// <summary>
/// Logical per-person status for one message (received/seen + reason), excluding the sender.
/// Keyed by the recipient's MembershipId rather than a bare account id: composite FKs below pin
/// this row's chat to both the message's chat and the membership's chat, and pin the membership to
/// the stated account, so no cross-chat or cross-account association can be stored.
/// </summary>
public class SyncMessageRecipientStateEntity
{
	public SyncMessageRecipientStateEntity(string messageId, string chatId, string membershipId, string accountId, long version)
	{
		MessageId = messageId;
		ChatId = chatId;
		MembershipId = membershipId;
		AccountId = accountId;
		Version = version;
	}

	private SyncMessageRecipientStateEntity() { }

	public string MessageId { get; set; } = null!;

	/// <summary>Denormalized from Message/Membership for the composite FKs.</summary>
	public string ChatId { get; set; } = null!;

	public string MembershipId { get; set; } = null!;

	/// <summary>Denormalized from Membership for the composite FK and for query convenience.</summary>
	public string AccountId { get; set; } = null!;

	public long Version { get; set; }

	public DateTime? ReceivedAtUtc { get; set; }

	public DateTime? SeenAtUtc { get; set; }

	public SyncSeenReason? SeenReason { get; set; }

	public virtual SyncMessageEntity Message { get; set; } = null!;
	public virtual SyncMembershipEntity Membership { get; set; } = null!;
}
