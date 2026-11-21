using Database.Context;
using Services.Sessions.Models;

namespace Services.Sessions.Interface;

/// <summary>
/// The account-scoped trust decisions and mutations plan/05-device-sessions.md describes: who needs
/// approval to enroll, and what explicit revocation does. Deliberately does not know about challenges,
/// device proofs or tokens - see <see cref="IDeviceEnrollmentCoordinator"/> for the orchestration that
/// combines this with those. Every method here either reads consistently or mutates atomically under
/// an explicit row lock the caller's own transaction holds for the duration of the decision.
/// </summary>
public interface IDeviceTrustService
{
	/// <summary>
	/// Reads the account's current trusted-device count and <see cref="Database.Entities.Sync.SyncAccountEntity.RevocationBarrierActive"/>
	/// together, plus whether <paramref name="deviceRegistrationId"/> ITSELF is already trusted (see
	/// <see cref="EnrollmentRequirementKind.AlreadyTrustedReauthentication"/>). Callers that will act on
	/// this (enroll a device) must re-run it again at commit under a row lock on the account
	/// (<see cref="TryTrustPendingDeviceAsync"/> does this itself) - this standalone read is for the
	/// earlier "what challenge purpose should I even issue" decision, which is inherently advisory (a
	/// concurrent revocation between this call and commit is exactly what the commit-time re-check
	/// exists to catch).
	/// </summary>
	Task<EnrollmentRequirement> DetermineEnrollmentRequirementAsync(AppDbContext db, string accountId, string deviceRegistrationId, CancellationToken ct = default);

	/// <summary>
	/// Locks the account row, re-runs the exact same decision <see cref="DetermineEnrollmentRequirementAsync"/>
	/// makes, and - only if it still permits enrollment given <paramref name="hasValidApproval"/> -
	/// flips the Pending <see cref="Database.Entities.Sync.SyncDeviceRegistrationEntity"/> row to
	/// Trusted (stamping <see cref="Database.Entities.Sync.SyncDeviceRegistrationEntity.IsFirstDevice"/>/
	/// <see cref="Database.Entities.Sync.SyncDeviceRegistrationEntity.ApprovedByDeviceRegistrationId"/>
	/// as appropriate). Does not call SaveChanges/commit - the caller
	/// (<see cref="IDeviceEnrollmentCoordinator"/>) does that together with the challenge consumption
	/// and new session it commits in the same transaction. The account row lock is what makes "two
	/// concurrent first-device enrollments for the same account" resolve to exactly one winner (the
	/// loser re-evaluates under the now-current state and is told DeviceApproval is required).
	/// </summary>
	/// <exception cref="DeviceApprovalRequiredException">Approval is required but <paramref name="hasValidApproval"/> is false, or no trusted device exists to approve at all.</exception>
	Task<EnrollmentRequirement> TryTrustPendingDeviceAsync(
		AppDbContext db, string accountId, string deviceRegistrationId, string? approvedByDeviceRegistrationId,
		bool hasValidApproval, DateTime now, CancellationToken ct = default);

	/// <summary>
	/// Explicit revocation: ONE transaction (opened and committed internally - see below) that locks the
	/// account row FIRST (the exact same lock, in the exact same order, as <see cref="TryTrustPendingDeviceAsync"/>
	/// takes), re-validates the ACTING device's own current trust/epoch under that lock, then atomically
	/// bumps the target device's TrustVersion, marks it Revoked, ends every session it has (clearing
	/// refresh capability), and sets the account's sticky <see cref="Database.Entities.Sync.SyncAccountEntity.RevocationBarrierActive"/>
	/// flag - all before committing. The shared account-row lock (held for the whole decision, exactly
	/// like enrollment) is what makes "revoke the account's last trusted device" and "a concurrent
	/// enrollment decision for the same account" genuinely serialize against each other instead of each
	/// reading a transient, inconsistent intermediate state - see docs referenced from the stage's fix
	/// report for the reproduced race this closes. Commits internally (the caller must NOT additionally
	/// call <c>SaveChangesAsync</c>/open its own transaction around this) and sends no live notification
	/// itself - the caller notifies (<see cref="ISessionRevocationNotifier"/>) only after this returns,
	/// i.e. only once the mutation is durable.
	/// </summary>
	/// <param name="actingDeviceRegistrationId">The device performing the revocation (from the caller's own authenticated app-token claims) - re-validated as currently Trusted, at <paramref name="actingTrustVersion"/>, under the same account lock, immediately before the target is touched.</param>
	/// <param name="actingTrustVersion">The acting device's TrustVersion as claimed by the caller's own access token - must still match the device's live TrustVersion at commit, closing the gap between an authorization check earlier in the request pipeline and this mutation.</param>
	/// <param name="actingSessionId">
	/// The SPECIFIC session the caller's own access token is bound to (its SessionId claim) - REQUIRED,
	/// never optional: the one production caller (<see cref="Turbo.Controllers.DeviceSession.Controller.DeviceSessionController.Revoke"/>)
	/// always has one (any token reaching that action already passed <c>AppSessionRequirement</c>, which
	/// mandates a SessionId claim), and this interface no longer offers a null/default-valued way to skip
	/// the check below - a hypothetical future caller cannot silently opt back into the old, unlocked
	/// behavior merely by omitting an argument (independent-review-flagged API-shape footgun, fixed).
	/// Re-validated with a real <c>FOR UPDATE</c> row lock (not a plain read), after both the acting and
	/// target device locks (account -&gt; device -&gt; session tier order, consistent with
	/// <see cref="Service.RefreshTokenService.RotateAsync"/>/<see cref="Service.SessionTrustChecker.CheckAndLockDeviceForMutationAsync"/>),
	/// immediately before the target is touched - closes a gap <paramref name="actingTrustVersion"/> alone
	/// does not: <see cref="Service.RefreshTokenService.RotateAsync"/>'s reuse-detection path can end a
	/// session WITHOUT ever revoking (or touching) the device row at all, so an acting device can still
	/// look "Trusted" at the expected TrustVersion even though the exact session this request's token
	/// claims has, independently, already ended. The lock (not merely the read) is what makes this
	/// genuinely serialize against a concurrent <see cref="Service.RefreshTokenService.RotateAsync"/>
	/// reuse-detection commit for the SAME session, rather than racing it.
	/// </param>
	/// <returns>The ids of every session that was ended, for the caller to notify.</returns>
	/// <exception cref="SessionNotTrustedException">The acting device is no longer currently trusted (or its trust epoch moved) at commit, the acting session has ended, or the target device does not exist for this account or is already revoked.</exception>
	Task<IReadOnlyList<string>> RevokeDeviceAsync(
		AppDbContext db, string accountId, string actingDeviceRegistrationId, long actingTrustVersion,
		string targetDeviceRegistrationId, DateTime now, string actingSessionId, CancellationToken ct = default);
}
