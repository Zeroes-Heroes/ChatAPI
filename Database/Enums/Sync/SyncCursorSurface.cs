namespace Database.Enums.Sync;

/// <summary>
/// Which enumeration a cursor belongs to. Part of the cursor's binding, so a token minted for one
/// surface can never be replayed on another: a history cursor presented to the delta endpoint is as
/// invalid as a foreign one.
/// </summary>
public enum SyncCursorSurface
{
	/// <summary>The fixed boundary set of a sync session (SyncBoundaryPage), paged by ordinal.</summary>
	Boundary = 0,

	/// <summary>The account's chat catalog: the fixed boundary set as chat snapshots in Full mode, the catalog journal in Delta mode.</summary>
	Catalog = 1,

	/// <summary>One chat scope's permitted content, read from the entity tables in message-sequence order (full restoration).</summary>
	History = 2,

	/// <summary>One stream's changes since a position, read from the journal.</summary>
	Delta = 3
}
