using Database.Context;
using WireContracts = Services.Sync.Contracts;

namespace Services.Sessions.Interface;

/// <summary>
/// Thin orchestration over the focused primitives (<see cref="IDeviceChallengeService"/>,
/// <see cref="IDeviceProofVerifier"/>, <see cref="IDeviceTrustService"/>,
/// <see cref="IRefreshTokenService"/>, <see cref="ISessionTokenIssuer"/>,
/// <see cref="ISyncAccountProvisioningService"/>) for the two device-enrollment HTTP endpoints. Holds
/// no business rules of its own beyond call ordering and transaction boundaries - every actual
/// decision/invariant lives in one of the composed services.
/// </summary>
public interface IDeviceEnrollmentCoordinator
{
	/// <summary>Resolves/creates the caller's AccountContextId and issues the appropriate Enrollment/DeviceApproval challenge for a (possibly brand new) device registration.</summary>
	/// <exception cref="DeviceApprovalRequiredException">No currently trusted device exists to approve this registration (and it is not a genuine first device).</exception>
	Task<WireContracts.DeviceChallenge> RequestEnrollmentChallengeAsync(
		AppDbContext db, int legacyUserId, string deviceRegistrationId, string keyId, byte[] spki, CancellationToken ct = default);

	/// <summary>
	/// Consumes the challenge, verifies the device proof (and approval, if supplied), applies the
	/// trust decision, creates the session and mints tokens - all in one transaction.
	/// </summary>
	/// <exception cref="ChallengeInvalidException">Unknown/expired/already-consumed challenge.</exception>
	/// <exception cref="DeviceProofInvalidException">The device proof signature does not verify.</exception>
	/// <exception cref="DeviceApprovalRequiredException">Approval is required but missing/invalid, or no longer available at commit.</exception>
	Task<WireContracts.SessionTokens> RegisterDeviceAsync(
		AppDbContext db, int legacyUserId, string localInstanceId,
		WireContracts.DeviceProof proof, WireContracts.DeviceApproval? approval, CancellationToken ct = default);
}
