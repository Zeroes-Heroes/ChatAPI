namespace Database.Enums.Sync;

/// <summary>Why a membership stint stopped being active. Does not include "Added" since that is the start of a stint, not an end.</summary>
public enum SyncMembershipEndReason
{
	Left = 0,
	Removed = 1
}
