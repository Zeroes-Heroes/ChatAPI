using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Crypto;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using WireContracts = Services.Sync.Contracts;

namespace Services.Sessions.Service;

public sealed class DeviceEnrollmentCoordinator(
	TimeProvider timeProvider,
	ISyncAccountProvisioningService accountProvisioning,
	IDeviceChallengeService challengeService,
	IDeviceProofVerifier proofVerifier,
	IDeviceTrustService trustService,
	IRefreshTokenService refreshTokenService,
	ISessionTokenIssuer tokenIssuer) : IDeviceEnrollmentCoordinator
{
	public async Task<WireContracts.DeviceChallenge> RequestEnrollmentChallengeAsync(
		AppDbContext db, int legacyUserId, string deviceRegistrationId, string keyId, byte[] spki,
		string localInstanceId, CancellationToken ct = default)
	{
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;
		SyncAccountEntity account = await accountProvisioning.GetOrCreateActiveAccountAsync(db, legacyUserId, now, ct);

		EnrollmentRequirement requirement = await trustService.DetermineEnrollmentRequirementAsync(db, account.Id, deviceRegistrationId, ct);
		if (requirement.Kind == EnrollmentRequirementKind.BlockedNoTrustedDeviceAvailable)
		{
			throw new DeviceApprovalRequiredException("No currently trusted device is available to approve this registration.");
		}

		return await challengeService.IssueEnrollmentChallengeAsync(db, account.Id, deviceRegistrationId, keyId, spki, localInstanceId, requirement.Purpose, ct);
	}

	public async Task<WireContracts.SessionTokens> RegisterDeviceAsync(
		AppDbContext db, int legacyUserId, string localInstanceId,
		WireContracts.DeviceProof proof, WireContracts.DeviceApproval? approval, CancellationToken ct = default)
	{
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;

		await using var tx = await db.Database.BeginTransactionAsync(ct);

		SyncDeviceChallengeEntity challenge = await challengeService.ConsumeAsync(db, proof.ChallengeId, ct);

		if (challenge.Purpose != ChallengePurpose.Enrollment && challenge.Purpose != ChallengePurpose.DeviceApproval)
		{
			throw new ChallengeInvalidException("Challenge purpose is not valid for device registration.");
		}

		// Stage 06: the challenge must belong to this phone's CURRENT context. A challenge issued before a
		// fresh start (RecoveryEnrollmentCoordinator applies the same rule) would otherwise still be
		// processed and answer "approval required", leaving a device retrying an ended context instead of
		// enrolling into the new one - and any session it won there is one SessionTrustChecker rejects on
		// its next request anyway.
		SyncAccountEntity account = await db.SyncAccounts.SingleAsync(a => a.Id == challenge.AccountId, ct);
		if (account.LegacyUserId != legacyUserId || account.EndedAtUtc is not null)
		{
			throw new SessionNotTrustedException("Challenge does not belong to the authenticated bootstrap identity's current context.");
		}

		DeviceProofBinding binding = new(
			challenge.Id, challenge.Purpose.ToString(), challenge.AccountId, challenge.DeviceRegistrationId,
			challenge.KeyId, challenge.PublicKeySpki, challenge.Nonce, RfcTimestamp.Format(challenge.ExpiresAtUtc));

		if (!Base64Url.TryDecode(proof.Signature, out byte[] proofSignature))
		{
			throw new DeviceProofInvalidException("Signature is not canonical base64url.");
		}
		proofVerifier.VerifyDeviceProof(binding, challenge.PublicKeySpki, proofSignature);

		// Stage 06: the local copy is bound by the challenge row itself, because the pinned
		// chat-device-approval-v1 array has no field for it (see SyncDeviceChallengeEntity.LocalInstanceId).
		// Checked before any trust mutation: an approval granted for one local copy must not register a
		// different one.
		if (challenge.LocalInstanceId is null || !string.Equals(challenge.LocalInstanceId, localInstanceId, StringComparison.Ordinal))
		{
			throw new ChallengeInvalidException("Challenge was not issued for this local instance.");
		}

		// Either the requesting device carried the approval itself (the stage-05 shape, still accepted)
		// or - the normal stage-06 path - a trusted device recorded it server-side against this exact
		// challenge, because the two devices have no channel between them.
		ApprovalAssertion? assertion = approval is not null
			? await VerifySuppliedApprovalAsync(db, challenge, binding, approval, ct)
			: await ConsumeRecordedApprovalAsync(db, challenge, binding, now, ct);

		// Re-decides and mutates under an account row lock, re-reading the approver's own trust and
		// session liveness there - the authoritative, commit-time check.
		await trustService.TryTrustPendingDeviceAsync(db, challenge.AccountId, challenge.DeviceRegistrationId, assertion, recoveryProofVerified: false, now, ct);

		long trustVersion = await db.SyncDeviceRegistrations.AsNoTracking()
			.Where(d => d.AccountId == challenge.AccountId && d.Id == challenge.DeviceRegistrationId)
			.Select(d => d.TrustVersion)
			.SingleAsync(ct);

		await LocalInstanceRegistrar.EnsureRecordedAsync(db, challenge.AccountId, challenge.DeviceRegistrationId, localInstanceId, now, ct);

		NewSession newSession = await refreshTokenService.CreateSessionAsync(
			db, challenge.AccountId, challenge.DeviceRegistrationId, localInstanceId, trustVersion, now, ct);

		await db.SaveChangesAsync(ct);
		await tx.CommitAsync(ct);

		SessionIdentity identity = new(legacyUserId, challenge.AccountId, newSession.SessionId, challenge.DeviceRegistrationId, localInstanceId, newSession.TrustVersion);
		return tokenIssuer.IssueTokens(identity, newSession.RawRefreshToken, newSession.RefreshTokenExpiresAtUtc, now);
	}

	/// <summary>
	/// The approval travelled with the registering device itself. The approving device's key is still
	/// read fresh from the database - never from <c>ApprovingSession</c>'s own claims - per
	/// docs/sync-contract-v1.md: "supplied ApprovingSession is not authority". An unverifiable signature
	/// yields <c>SignatureVerified = false</c> rather than an exception, so the commit-time decision
	/// (which may not need an approval at all) stays the single place that refuses.
	/// </summary>
	private async Task<ApprovalAssertion> VerifySuppliedApprovalAsync(
		AppDbContext db, SyncDeviceChallengeEntity challenge, DeviceProofBinding binding,
		WireContracts.DeviceApproval approval, CancellationToken ct)
	{
		// The approval must be for the very challenge this registration consumed; the stage-05 code
		// never compared the two ids.
		if (!string.Equals(approval.ChallengeId, challenge.Id, StringComparison.Ordinal))
		{
			throw new DeviceProofInvalidException("Approval is not bound to the challenge being registered.");
		}

		string approverId = approval.ApprovingSession.Device.DeviceRegistrationId;
		SyncDeviceRegistrationEntity? approver = await db.SyncDeviceRegistrations.AsNoTracking()
			.SingleOrDefaultAsync(d => d.AccountId == challenge.AccountId && d.Id == approverId, ct);

		bool verified = false;
		if (approver is not null && Base64Url.TryDecode(approval.Signature, out byte[] approvalSignature))
		{
			try
			{
				proofVerifier.VerifyDeviceApproval(binding, approver.PublicKeySpki, approvalSignature);
				verified = true;
			}
			catch (DeviceProofInvalidException)
			{
				verified = false;
			}
		}

		return new ApprovalAssertion(approverId, approval.ApprovingSession.SessionId, verified);
	}

	/// <summary>
	/// The stage-06 path: a trusted device already recorded its approval against this exact challenge
	/// (<see cref="IDeviceApprovalService"/>), because the approving and registering devices have no
	/// channel between them. Everything the grant claims is re-compared against the challenge row this
	/// registration actually consumed - a grant can never authorize a different device, key, local copy
	/// or lifetime than the one its approver saw - and the signature is re-verified against the
	/// approver's CURRENT registered key, so a key rotation after approval invalidates it rather than
	/// being inherited. Returns null when there is no usable grant: whether that is fatal is the
	/// commit-time trust decision's call (a genuine first device needs no approval at all).
	/// </summary>
	private async Task<ApprovalAssertion?> ConsumeRecordedApprovalAsync(
		AppDbContext db, SyncDeviceChallengeEntity challenge, DeviceProofBinding binding, DateTime now, CancellationToken ct)
	{
		SyncDeviceApprovalGrantEntity? grant = await db.SyncDeviceApprovalGrants.AsNoTracking()
			.FirstOrDefaultAsync(g => g.ChallengeId == challenge.Id, ct);
		if (grant is null)
		{
			return null;
		}

		bool bindsThisRequest = grant.AccountId == challenge.AccountId
			&& grant.PendingDeviceRegistrationId == challenge.DeviceRegistrationId
			&& grant.PendingKeyId == challenge.KeyId
			&& grant.PendingPublicKeySpki.SequenceEqual(challenge.PublicKeySpki)
			&& grant.PendingLocalInstanceId == challenge.LocalInstanceId
			&& grant.ExpiresAtUtc == challenge.ExpiresAtUtc;
		if (!bindsThisRequest || grant.ExpiresAtUtc <= now)
		{
			return null;
		}

		SyncDeviceRegistrationEntity? approver = await db.SyncDeviceRegistrations.AsNoTracking()
			.SingleOrDefaultAsync(d => d.AccountId == grant.AccountId && d.Id == grant.ApprovingDeviceRegistrationId, ct);
		if (approver is null)
		{
			return null;
		}

		try
		{
			proofVerifier.VerifyDeviceApproval(binding, approver.PublicKeySpki, grant.Signature);
		}
		catch (DeviceProofInvalidException)
		{
			return null;
		}

		// One atomic conditional claim, exactly like the challenge's own consumption: two concurrent
		// register attempts carrying the same grant can never both consume it. Participates in this
		// method's caller's ambient transaction, so an abandoned registration rolls the claim back
		// together with everything else rather than burning the approval.
		int claimed = await db.Database.ExecuteSqlInterpolatedAsync(
			$"""
			UPDATE "SyncDeviceApprovalGrants" SET "ConsumedAtUtc" = {now}
			WHERE "ChallengeId" = {grant.ChallengeId} AND "ConsumedAtUtc" IS NULL
			""", ct);
		if (claimed != 1)
		{
			return null;
		}

		return new ApprovalAssertion(grant.ApprovingDeviceRegistrationId, grant.ApprovingSessionId, true);
	}
}
