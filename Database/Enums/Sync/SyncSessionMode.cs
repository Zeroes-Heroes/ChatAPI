namespace Database.Enums.Sync;

/// <summary>
/// Which sync one handshake fixed. Full persists until the account/local-instance initial-restore
/// marker (SyncLocalInstances.InitialRestoreCompletedAtUtc) is set - "Full mode persists until the
/// account/local-instance initial restore marker completes, including after restart"
/// (docs/sync-contract-v1.md) - so a restart in the middle keeps the whole initial barrier.
/// </summary>
public enum SyncSessionMode
{
	Full = 0,
	Delta = 1
}
