namespace Database.Enums.Sync;

/// <summary>
/// Storage counterpart of the wire contract's PositionExchangeKind. The three share only transport
/// batching, never their meaning: Resume uploads every persisted local position, Acknowledge confirms
/// continuity up to a fixed boundary, Check compares positions for periodic reconciliation.
/// </summary>
public enum SyncPositionExchangeKind
{
	Resume = 0,
	Acknowledge = 1,
	Check = 2
}
