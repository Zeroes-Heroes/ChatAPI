namespace Database.Enums.Sync;

/// <summary>Storage-side mirror of <c>Services.Sync.Contracts.ChallengePurpose</c> (same names/order) - kept as its own DB enum, not a shared type, for the same reason every other Sync* storage enum in this project is separate from its wire contract counterpart (independent storage/wire evolution).</summary>
public enum ChallengePurpose
{
	Enrollment = 0,
	Recovery = 1,
	SessionBinding = 2,
	DeviceApproval = 3
}
