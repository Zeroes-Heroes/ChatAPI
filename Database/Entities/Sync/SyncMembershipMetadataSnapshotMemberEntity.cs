using Database.Enums.Sync;

namespace Database.Entities.Sync;

/// <summary>
/// One other participant who was present in the chat at the moment a membership stint was frozen.
/// Captured once, alongside SyncMembershipMetadataSnapshotEntity, so the frozen member's archive can
/// show who was in the chat at exit without depending on the live, still-changing Membership rows.
/// </summary>
public class SyncMembershipMetadataSnapshotMemberEntity
{
	public SyncMembershipMetadataSnapshotMemberEntity(string membershipId, string chatId, string participantAccountId, string? participantMembershipIdAtSnapshot, SyncMembershipState participantStateAtSnapshot)
	{
		MembershipId = membershipId;
		ChatId = chatId;
		ParticipantAccountId = participantAccountId;
		ParticipantMembershipIdAtSnapshot = participantMembershipIdAtSnapshot;
		ParticipantStateAtSnapshot = participantStateAtSnapshot;
	}

	private SyncMembershipMetadataSnapshotMemberEntity() { }

	/// <summary>The frozen membership stint that owns this snapshot (whose archive this describes).</summary>
	public string MembershipId { get; set; } = null!;

	/// <summary>Denormalized from Membership for the composite FK; pins every reference below to the same chat.</summary>
	public string ChatId { get; set; } = null!;

	public string ParticipantAccountId { get; set; } = null!;

	/// <summary>The other participant's own membership stint at the time of the snapshot, if they had an active one.</summary>
	public string? ParticipantMembershipIdAtSnapshot { get; set; }

	public SyncMembershipState ParticipantStateAtSnapshot { get; set; }

	public virtual SyncMembershipMetadataSnapshotEntity MetadataSnapshot { get; set; } = null!;
	public virtual SyncAccountEntity ParticipantAccount { get; set; } = null!;
	public virtual SyncMembershipEntity? ParticipantMembership { get; set; }
}
