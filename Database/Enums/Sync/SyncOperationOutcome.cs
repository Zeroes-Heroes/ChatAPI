namespace Database.Enums.Sync;

/// <summary>Only terminal outcomes are persisted. Unknown/Retryable are transient responses, never stored.</summary>
public enum SyncOperationOutcome
{
	Accepted = 0,
	Rejected = 1
}
