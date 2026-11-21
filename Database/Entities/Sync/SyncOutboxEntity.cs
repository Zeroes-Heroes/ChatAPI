using Database.Enums.Sync;

namespace Database.Entities.Sync;

/// <summary>
/// Outbox row for reliable post-commit publish to the broker. Written in the same transaction as
/// the message/key/result/logical changes; the network publish itself happens afterwards, claimed
/// atomically via LeaseOwner/LeasedUntilUtc so competing replicas don't double-publish.
/// </summary>
public class SyncOutboxEntity
{
	public SyncOutboxEntity(string chatId, string messageId, long changeSequence, DateTime createdAtUtc)
	{
		ChatId = chatId;
		MessageId = messageId;
		ChangeSequence = changeSequence;
		Status = SyncOutboxStatus.Pending;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncOutboxEntity() { }

	public long Id { get; set; }

	public string ChatId { get; set; } = null!;

	public string MessageId { get; set; } = null!;

	public long ChangeSequence { get; set; }

	public SyncOutboxStatus Status { get; set; }

	public int AttemptCount { get; set; }

	public DateTime? NextAttemptAtUtc { get; set; }

	public string? LastError { get; set; }

	public string? LeaseOwner { get; set; }

	public DateTime? LeasedUntilUtc { get; set; }

	public DateTime? PublishedAtUtc { get; set; }

	public DateTime CreatedAtUtc { get; set; }

	public virtual SyncChatEntity Chat { get; set; } = null!;
	public virtual SyncMessageEntity Message { get; set; } = null!;
}
