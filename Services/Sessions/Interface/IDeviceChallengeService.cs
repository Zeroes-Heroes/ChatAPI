using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using WireContracts = Services.Sync.Contracts;

namespace Services.Sessions.Interface;

/// <summary>
/// Issues and atomically consumes the persisted, one-use device challenges docs/sync-contract-v1.md
/// describes. Deliberately thin and caller-owns-the-transaction, matching
/// <see cref="Services.Keys.Interface.IProtectedMessageKeyStore"/>'s own convention: every method
/// takes the caller's <see cref="AppDbContext"/> and adds/updates rows on it without calling
/// <c>SaveChangesAsync</c> itself, so a caller (device enrollment, session refresh) can commit the
/// challenge consumption atomically together with the registration/session mutation it authorizes.
/// </summary>
public interface IDeviceChallengeService
{
	/// <summary>
	/// Issues an Enrollment or DeviceApproval challenge for a (possibly brand new) device
	/// registration, creating the <see cref="SyncDeviceRegistrationEntity"/> row in
	/// <see cref="DeviceTrustState.Pending"/> if one does not already exist for
	/// <paramref name="deviceRegistrationId"/> - see that entity's own doc comment. If a Pending or
	/// Revoked row already exists for this id, its key is NOT overwritten with
	/// <paramref name="spki"/>/<paramref name="keyId"/> (a client cannot silently rebind an existing
	/// registration id to a different key via a fresh challenge) - the existing row's own key is
	/// reused for the new challenge instead.
	/// </summary>
	Task<WireContracts.DeviceChallenge> IssueEnrollmentChallengeAsync(
		AppDbContext db, string accountId, string deviceRegistrationId, string keyId, byte[] spki,
		ChallengePurpose purpose, CancellationToken ct = default);

	/// <summary>Issues a SessionBinding challenge for an existing device, using that device's own currently-registered key (never a key supplied by this call) - see docs/sync-contract-v1.md: "Refresh uses SessionBinding challenges additionally bound server-side to SessionId/RefreshOperationId."</summary>
	/// <exception cref="SessionNotTrustedException">No such device registration exists for this account.</exception>
	Task<WireContracts.DeviceChallenge> IssueSessionBindingChallengeAsync(
		AppDbContext db, string accountId, string deviceRegistrationId, string sessionId, string refreshOperationId,
		CancellationToken ct = default);

	/// <summary>
	/// Atomically claims a challenge by a single conditional UPDATE ("... WHERE ConsumedAtUtc IS
	/// NULL"), so two concurrent consumption attempts for the same challenge can never both succeed.
	/// Also validates expiry. Returns the now-consumed row (with its server-bound fields) for the
	/// caller to build a <see cref="Models.DeviceProofBinding"/> from.
	/// </summary>
	/// <exception cref="ChallengeInvalidException">Unknown id, already consumed, or expired.</exception>
	Task<SyncDeviceChallengeEntity> ConsumeAsync(AppDbContext db, string challengeId, CancellationToken ct = default);
}
