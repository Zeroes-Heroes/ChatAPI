namespace Database.Sequencing;

/// <summary>
/// The sequence/position numbering convention shared by every chat-scoped and account-scoped
/// stream in this model (SyncMessages.MessageSequence/ChangeSequence, SyncChatChanges.ChangeSequence,
/// SyncCatalogEntries.CatalogSequence) and by the wire contract's position-reached values
/// (StreamPosition/AppliedThrough, AcceptedMessage.OwnSendThroughSequence):
///
/// 0 is permanently reserved for "nothing in this stream has been applied/sent yet" (the
/// documented resume default for a newly authorized stream per docs/sync-contract-v1.md, and the
/// "no older message exists to mark" case for OwnSendThroughSequence). The first real item ever
/// committed to a stream is always numbered 1, seeded by SyncChatSequenceCounterEntity/
/// SyncAccountCatalogCounterEntity and enforced by a "&gt; 0" CHECK constraint on every persisted
/// sequence column. This keeps "0 items applied" and "item #1 applied" distinguishable on the
/// wire, which an inclusive "position reached" cursor compared with "&gt; reportedPosition" depends
/// on to avoid skipping the first committed item.
/// </summary>
public static class SyncSequenceConventions
{
	/// <summary>The wire value meaning "nothing in this stream has been applied/sent yet".</summary>
	public const long EmptyStreamPosition = 0;

	/// <summary>The first real sequence value any counter in this model ever hands out.</summary>
	public const long FirstSequence = 1;

	/// <summary>
	/// OwnSendThroughSequence is a pure, immutable function of the message's own MessageSequence:
	/// per MESSAGE_SYNC_ANALYSIS.md, every older incoming message in the same chat (sequence less
	/// than this message's own) is logically marked read as of this message's own successful
	/// commit, so the boundary is always (this message's own MessageSequence) - 1. For the first
	/// message ever sent in a chat (MessageSequence == FirstSequence == 1) this yields 0 -
	/// EmptyStreamPosition, correctly meaning "no older message exists to mark", not a sentinel
	/// invented for this case. Because it depends only on this message's own already-immutable
	/// MessageSequence (never on wall-clock time or on what else has happened since), retrying the
	/// same accepted operation - which must return the original persisted result, never recompute
	/// it - always yields the same value.
	/// </summary>
	public static long OwnSendThroughSequence(long messageSequence)
	{
		if (messageSequence < FirstSequence)
			throw new ArgumentOutOfRangeException(nameof(messageSequence), messageSequence, $"A message's own MessageSequence must be >= {FirstSequence}.");

		return messageSequence - 1;
	}
}
