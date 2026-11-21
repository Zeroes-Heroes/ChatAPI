using Database.Sequencing;

namespace Database.Entities.Sync;

/// <summary>
/// Holds the next message and change sequence for one chat. Must only be advanced through
/// Database.Sequencing.SyncSequenceAllocator (see remarks there for the commit-order invariant).
/// Both counters live in the same row on purpose: a new message and a non-message chat change
/// (e.g. a membership change) contend for the same lock, giving the chat a single well-defined
/// commit order across both kinds of writes.
///
/// Seeded at Database.Sequencing.SyncSequenceConventions.FirstSequence (1), never 0: 0 is
/// permanently reserved as the wire sentinel for "nothing in this stream has been applied/sent
/// yet" (the documented resume default for a newly authorized stream), so the first real message/
/// change in the chat must be numbered 1, not 0. Enforced by a "> 0" CHECK constraint in
/// SyncChatSequenceCounterConfiguration so this invariant cannot regress at the database level.
/// </summary>
public class SyncChatSequenceCounterEntity
{
	public SyncChatSequenceCounterEntity(string chatId)
	{
		ChatId = chatId;
		NextMessageSequence = SyncSequenceConventions.FirstSequence;
		NextChangeSequence = SyncSequenceConventions.FirstSequence;
	}

	private SyncChatSequenceCounterEntity() { }

	public string ChatId { get; set; } = null!;

	public long NextMessageSequence { get; set; }

	public long NextChangeSequence { get; set; }

	/// <summary>
	/// Stage 13: how far this chat's change journal (SyncChatChanges) has been cleaned. 0 means nothing
	/// was ever pruned. A delta read that would have to start at or below this value cannot prove it is
	/// not skipping a change, so it answers CursorInvalid and the device reconciles its allowed state
	/// from the entity tables instead - which is what "Журналът се почиства само при възстановимост на
	/// необходимото състояние" means in practice: the journal is a liveness mechanism, not the record.
	/// </summary>
	public long PrunedThroughChangeSequence { get; set; }

	public virtual SyncChatEntity Chat { get; set; } = null!;
}
