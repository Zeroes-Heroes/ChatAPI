namespace Database.Enums.Sync;

/// <summary>Storage counterpart of the wire contract's StreamKind: the per-account catalog stream or one chat stream.</summary>
public enum SyncStreamKind
{
	Catalog = 0,
	Chat = 1
}
