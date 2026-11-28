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

	/// <summary>
	/// Durable delivery discriminator; independent of the prunable chat journal, so a task claimed long
	/// after its journal entry was pruned still knows what it delivers.
	///
	/// The column has a database default of Message (1) so the additive migration could fill existing
	/// rows. One consequence of that default: EF omits a property from the INSERT when its value equals
	/// the CLR default, so a row explicitly set to <see cref="SyncChangeKind.Chat"/> (0) would be stored
	/// as Message. No producer queues a Chat row - the three kinds written are Message, RecipientState
	/// and DeviceState - and delivery refuses any other kind rather than guessing, but a future Chat
	/// producer must drop the model-level default first.
	/// </summary>
	public SyncChangeKind ChangeKind { get; set; } = SyncChangeKind.Message;

	/// <summary>
	/// The subject of a status delivery: the recipient's account context for
	/// <see cref="SyncChangeKind.RecipientState"/>, the reporting local instance for
	/// <see cref="SyncChangeKind.DeviceState"/>. Null for a message.
	///
	/// Exactly ONE id, by design rather than by capacity: this is a pointer at the row that changed, and
	/// one reference identifies it. The value is always copied from a row the server already resolved, and
	/// every persisted id column is varchar(100) like this one, so a single id can never be truncated
	/// here; a PAIR is what would not fit, which is why the device registration behind a local instance is
	/// read back from SyncMessageDeviceStates instead of being stored alongside it.
	/// </summary>
	public string? SecondaryId { get; set; }

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
