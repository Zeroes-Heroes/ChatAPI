namespace Services.Sync.Interface;

/// <summary>
/// Bounded cleanup of the two things a sync leaves behind: the change journals and the fixed state of
/// sessions that have expired.
///
/// The journals (plan/13-history-api.md: "Почистването на журнала запазва възстановимостта") are a
/// LIVENESS mechanism - they tell a caught-up device what moved - while the record itself is the entity
/// tables and the frozen archive snapshots, which the full restoration surfaces read and which this
/// never touches. Two floors make the cleanup safe rather than hopeful: nothing within the retention
/// window is removed, and nothing at or above the lowest position any LIVE sync session still needs.
/// What is removed raises the stream's pruned watermark in the SAME transaction, so a delta read that
/// would have had to start below it is answered CursorInvalid and the device reconciles its allowed
/// state instead of silently skipping a change.
/// </summary>
public interface ISyncRetention
{
	/// <summary>One bounded portion per call, so a sweep cannot monopolise a connection or a transaction.</summary>
	Task<JournalPruneOutcome> PruneJournalsAsync(CancellationToken ct = default);

	/// <summary>
	/// Removes expired sync sessions and, with them, their fixed boundaries, their cursors and their
	/// recorded exchanges (all cascade from the session row). Nothing a device has APPLIED depends on
	/// them: an expired session costs a new handshake, never a position - "Cursor lifetime follows sync
	/// session; expiration preserves applied positions."
	/// </summary>
	Task<int> PurgeExpiredSessionsAsync(CancellationToken ct = default);
}

/// <param name="ChatChangesRemoved">Rows removed from per-chat journals.</param>
/// <param name="CatalogEntriesRemoved">Rows removed from per-account catalog journals.</param>
/// <param name="StreamsVisited">How many streams this sweep considered, for the "did it keep up" question.</param>
public sealed record JournalPruneOutcome(int ChatChangesRemoved, int CatalogEntriesRemoved, int StreamsVisited);
