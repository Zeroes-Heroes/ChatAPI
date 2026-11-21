namespace Database.Enums.Sync;

/// <summary>
/// Lifecycle of a <see cref="Database.Entities.Sync.SyncDeviceRegistrationEntity"/>'s trust, per
/// plan/05-device-sessions.md. <c>Pending</c> exists only between "challenge issued" and "device-proof
/// verified at commit"; a registration row is created in this state (never NULL/absent) so the
/// one-use challenge can bind a concrete DeviceRegistrationId before trust is actually granted.
/// <c>Trusted</c> is the only state current-trust checks (HTTP policy, hub filter, refresh rotation)
/// accept. <c>Revoked</c> is terminal for this registration row (a revoked registration is never
/// re-trusted in place - plan/06-recovery.md's approval workflow creates a new registration instead).
/// </summary>
public enum DeviceTrustState
{
	Pending = 0,
	Trusted = 1,
	Revoked = 2
}
