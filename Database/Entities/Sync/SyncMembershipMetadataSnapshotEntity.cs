using Database.Enums.Sync;

namespace Database.Entities.Sync;

/// <summary>Chat metadata frozen at the moment a membership stint ended, so a removed participant's archive keeps showing the chat as it was.</summary>
public class SyncMembershipMetadataSnapshotEntity
{
	public SyncMembershipMetadataSnapshotEntity(string membershipId, string? chatName, SyncChatKind chatKindAtSnapshot, long capturedAtChangeSequence, DateTime capturedAtUtc)
	{
		MembershipId = membershipId;
		ChatName = chatName;
		ChatKindAtSnapshot = chatKindAtSnapshot;
		CapturedAtChangeSequence = capturedAtChangeSequence;
		CapturedAtUtc = capturedAtUtc;
	}

	private SyncMembershipMetadataSnapshotEntity() { }

	public string MembershipId { get; set; } = null!;

	public string? ChatName { get; set; }

	public SyncChatKind ChatKindAtSnapshot { get; set; }

	public long CapturedAtChangeSequence { get; set; }

	public DateTime CapturedAtUtc { get; set; }

	public virtual SyncMembershipEntity Membership { get; set; } = null!;
	public virtual ICollection<SyncMembershipMetadataSnapshotMemberEntity> Members { get; set; } = [];
}
