namespace Database.Entities.Sync;

/// <summary>
/// A frozen copy of one recipient's public status for one legacy message, taken once when a membership
/// stint was removed or left (plan/08-history-archives.md: "Премахване и напускане замразяват
/// разрешените съобщения, статуси и метаданни").
///
/// It exists for the same reason <see cref="SyncMembershipMessageStatusSnapshotEntity"/> does - the
/// live status rows keep changing for the chat's still-active members, so without a copy the frozen
/// member's archive would drift after the fact - but over the legacy <c>Messages</c>/<c>MessagesStatus</c>
/// tables, which is where every real message lives until stage 11. The sequence-space table stays
/// unwritten until <c>SyncMessages</c> exist.
/// </summary>
public class SyncMembershipLegacyStatusSnapshotEntity
{
	public SyncMembershipLegacyStatusSnapshotEntity(
		string membershipId, int legacyMessageId, int recipientLegacyUserId, long capturedAtChangeSequence)
	{
		MembershipId = membershipId;
		LegacyMessageId = legacyMessageId;
		RecipientLegacyUserId = recipientLegacyUserId;
		CapturedAtChangeSequence = capturedAtChangeSequence;
	}

	private SyncMembershipLegacyStatusSnapshotEntity() { }

	/// <summary>The frozen stint whose archive this status belongs to.</summary>
	public string MembershipId { get; set; } = null!;

	public int LegacyMessageId { get; set; }

	/// <summary>The person this status is about - never the device: the public mark is per recipient.</summary>
	public int RecipientLegacyUserId { get; set; }

	public DateTime? ReceivedAtUtc { get; set; }

	public DateTime? SeenAtUtc { get; set; }

	/// <summary>The chat change sequence of the removal/leave event this snapshot was captured at.</summary>
	public long CapturedAtChangeSequence { get; set; }

	public virtual SyncMembershipEntity Membership { get; set; } = null!;
	public virtual MessageEntity Message { get; set; } = null!;
	public virtual UserEntity RecipientUser { get; set; } = null!;
}
