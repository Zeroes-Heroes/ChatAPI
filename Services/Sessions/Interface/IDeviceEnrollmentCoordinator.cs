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
	/// <summary>Resolves/creates the caller's AccountContextId and issues the appropriate Enrollment/DeviceApproval challenge for a (possibly brand new) device registration. <paramref name="localInstanceId"/> is bound into the challenge row (stage 06) so an approval of this request also binds the requesting local copy.</summary>
	/// <exception cref="DeviceApprovalRequiredException">No currently trusted device exists to approve this registration (and it is not a genuine first device).</exception>
	/// <exception cref="SessionNotTrustedException">The registration id belongs to another account context - see <see cref="IDeviceChallengeService.IssueEnrollmentChallengeAsync"/>.</exception>
	/// <exception cref="DeviceIdentitySupersededException">...and that context is an ended one of this same identity - see <see cref="IDeviceChallengeService.IssueEnrollmentChallengeAsync"/>.</exception>
	Task<WireContracts.DeviceChallenge> RequestEnrollmentChallengeAsync(
		AppDbContext db, int legacyUserId, string deviceRegistrationId, string keyId, byte[] spki,
		string localInstanceId, CancellationToken ct = default);

	/// <summary>
	/// Consumes the challenge, verifies the device proof, resolves the approval (either carried in the
	/// request or - the normal stage-06 path - recorded server-side against this exact challenge by a
	/// trusted device, see <see cref="IDeviceApprovalService"/>), applies the trust decision, creates the
	/// session and mints tokens - all in one transaction.
	/// </summary>
	/// <exception cref="ChallengeInvalidException">Unknown/expired/already-consumed challenge, or one issued for a different local instance.</exception>
	/// <exception cref="DeviceProofInvalidException">The device proof signature does not verify.</exception>
	/// <exception cref="DeviceApprovalRequiredException">Approval is required but missing/invalid, or no longer available at commit.</exception>
	/// <exception cref="SessionNotTrustedException">The challenge belongs to a different bootstrap identity, or to an account context that has since ended (a fresh start); or the local instance id is already recorded under a different registration.</exception>
	/// <exception cref="DeviceIdentitySupersededException">The local instance id is recorded under a registration of an ENDED context of this same identity (LocalInstanceRegistrar).</exception>
	Task<WireContracts.SessionTokens> RegisterDeviceAsync(
		AppDbContext db, int legacyUserId, string localInstanceId,
		WireContracts.DeviceProof proof, WireContracts.DeviceApproval? approval, CancellationToken ct = default);
}
