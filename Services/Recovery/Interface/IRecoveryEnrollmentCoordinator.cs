using Database.Context;
using WireContracts = Services.Sync.Contracts;

namespace Services.Recovery.Interface;

/// <summary>
/// The bootstrap recovery route (MESSAGE_SYNC_ANALYSIS.md:153 "recovery тайна" alternative): a device
/// with no session, authenticated only by the legacy SMS/PIN credential, proves knowledge of the
/// recovery secret by signing the challenge with the recovery key it just opened out of the envelope.
/// That proof - verified server-side against the stored verification key - is what authorizes trust,
/// never a client-supplied flag. The sticky revocation barrier still outranks it
/// (<see cref="Services.Sessions.Interface.IDeviceTrustService"/> owns that decision).
/// </summary>
public interface IRecoveryEnrollmentCoordinator
{
	/// <summary>Issues a Recovery-purpose challenge for this exact device/key/local instance and returns the sealed candidates to try the secret against.</summary>
	/// <exception cref="RecoveryEnvelopeInvalidException">This account context has no recovery envelope at all.</exception>
	Task<WireContracts.RecoveryBootstrapChallenge> RequestRecoveryChallengeAsync(
		AppDbContext db, int legacyUserId, string deviceRegistrationId, string keyId, byte[] spki,
		string localInstanceId, CancellationToken ct = default);

	/// <summary>Consumes the challenge, verifies the device proof AND the recovery proof, applies the trust decision and issues a session - one transaction.</summary>
	/// <exception cref="Services.Sessions.ChallengeInvalidException">Unknown/expired/consumed challenge, wrong purpose, or a different local instance.</exception>
	/// <exception cref="Services.Sessions.DeviceProofInvalidException">Either proof fails, or they are not bound to the same challenge.</exception>
	/// <exception cref="Services.Sessions.DeviceApprovalRequiredException">The account's revocation barrier makes another trusted device's approval mandatory, even with a valid recovery proof.</exception>
	Task<WireContracts.SessionTokens> RegisterWithRecoveryAsync(
		AppDbContext db, int legacyUserId, WireContracts.RecoveryRegisterRequest request, CancellationToken ct = default);
}
