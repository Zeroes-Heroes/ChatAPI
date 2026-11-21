using Database.Enums.Sync;

namespace Services.Sessions.Models;

/// <summary>
/// Result of the atomic, account-scoped first-device/approval decision - plan/05-device-sessions.md:
/// "First device determination atomic/account-scoped" and "After explicit revocation ... requires
/// another currently trusted device ... Without it old context stays blocked."
/// </summary>
public enum EnrollmentRequirementKind
{
	/// <summary>No currently-trusted device exists for this account and the account has never had an explicit revocation - a genuine first device. No approval needed.</summary>
	FirstDevice,

	/// <summary>At least one currently-trusted device exists (a known-trusted device adding a sibling) or the account's revocation barrier is active with a trusted device still available to approve. A valid <see cref="Services.Sync.Contracts.DeviceApproval"/> is required at commit.</summary>
	ApprovalRequired,

	/// <summary>Approval is required (revocation barrier active, or any state short of a genuine first device) but zero currently-trusted devices exist to provide it. The old context stays blocked - this stage does not build the full recovery/approval workflow (plan/06-recovery.md); this is the tested, honest "blocked" outcome.</summary>
	BlockedNoTrustedDeviceAvailable,

	/// <summary>
	/// The requesting <c>DeviceRegistrationId</c> ITSELF is already <see cref="Database.Enums.Sync.DeviceTrustState.Trusted"/>
	/// for this account (its stored session/refresh state simply expired, ended, or was never
	/// established - not that the device was revoked). A fresh device-proof re-authenticates it
	/// directly: no other device's approval is required (and none could sensibly be obtained - a
	/// single already-trusted device cannot be asked to approve itself), and no trust-state mutation
	/// happens. A <see cref="Database.Enums.Sync.DeviceTrustState.Revoked"/> or previously-unknown
	/// registration never reaches this branch - see <see cref="Interface.IDeviceTrustService.DetermineEnrollmentRequirementAsync"/>.
	/// </summary>
	AlreadyTrustedReauthentication,
}

public sealed record EnrollmentRequirement(EnrollmentRequirementKind Kind, ChallengePurpose Purpose);
