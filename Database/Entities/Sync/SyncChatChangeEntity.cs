using Database.Enums.Sync;

namespace Database.Entities.Sync;

/// <summary>
/// Per-chat change journal, the "cursor" stream for StreamKind.Chat. ChangeSequence is allocated
/// by SyncSequenceAllocator from the same counter row as MessageSequence, so a reader walking this
/// table in sequence order never observes a position whose writing transaction has not committed.
/// A generic pointer log by design: ReferenceId/SecondaryId point at the detail row for Kind
/// (MessageId; MessageId+RecipientAccountId for RecipientState; MessageId+"device:instance" for
/// DeviceState; MembershipId for Unread/Membership/MembershipEvent). This journal is a liveness/
/// cursor mechanism and may be pruned once consumers are known to be caught up; the frozen-archive
/// snapshot tables (SyncMembershipMetadataSnapshot*, SyncMembershipMessageStatusSnapshot) are the
/// durable record and do not depend on this table surviving.
/// </summary>
public class SyncChatChangeEntity
{
	public SyncChatChangeEntity(string chatId, long changeSequence, SyncChangeKind kind, string referenceId, DateTime occurredAtUtc)
	{
		ChatId = chatId;
		ChangeSequence = changeSequence;
		Kind = kind;
		ReferenceId = referenceId;
		OccurredAtUtc = occurredAtUtc;
	}

	private SyncChatChangeEntity() { }

	public string ChatId { get; set; } = null!;

	public long ChangeSequence { get; set; }

	public SyncChangeKind Kind { get; set; }

	public string ReferenceId { get; set; } = null!;

	public string? SecondaryId { get; set; }

	/// <summary>Only set when Kind == MembershipEvent.</summary>
	public SyncMembershipEventKind? MembershipEventKind { get; set; }

	public DateTime OccurredAtUtc { get; set; }

	public virtual SyncChatEntity Chat { get; set; } = null!;
}
