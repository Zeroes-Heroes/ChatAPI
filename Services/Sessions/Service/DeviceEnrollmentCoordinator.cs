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
		AppDbContext db, int legacyUserId, string deviceRegistrationId, string keyId, byte[] spki, CancellationToken ct = default)
	{
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;
		SyncAccountEntity account = await accountProvisioning.GetOrCreateActiveAccountAsync(db, legacyUserId, now, ct);

		EnrollmentRequirement requirement = await trustService.DetermineEnrollmentRequirementAsync(db, account.Id, deviceRegistrationId, ct);
		if (requirement.Kind == EnrollmentRequirementKind.BlockedNoTrustedDeviceAvailable)
		{
			throw new DeviceApprovalRequiredException("No currently trusted device is available to approve this registration.");
		}

		return await challengeService.IssueEnrollmentChallengeAsync(db, account.Id, deviceRegistrationId, keyId, spki, requirement.Purpose, ct);
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

		SyncAccountEntity account = await db.SyncAccounts.SingleAsync(a => a.Id == challenge.AccountId, ct);
		if (account.LegacyUserId != legacyUserId)
		{
			throw new SessionNotTrustedException("Challenge does not belong to the authenticated bootstrap identity.");
		}

		DeviceProofBinding binding = new(
			challenge.Id, challenge.Purpose.ToString(), challenge.AccountId, challenge.DeviceRegistrationId,
			challenge.KeyId, challenge.PublicKeySpki, challenge.Nonce, RfcTimestamp.Format(challenge.ExpiresAtUtc));

		if (!Base64Url.TryDecode(proof.Signature, out byte[] proofSignature))
		{
			throw new DeviceProofInvalidException("Signature is not canonical base64url.");
		}
		proofVerifier.VerifyDeviceProof(binding, challenge.PublicKeySpki, proofSignature);

		string? approverId = null;
		bool hasValidApproval = false;
		if (approval is not null)
		{
			approverId = approval.ApprovingSession.Device.DeviceRegistrationId;

			// The approving device's key/trust is read fresh from the database - never from
			// ApprovingSession's own claims - per docs/sync-contract-v1.md: "supplied ApprovingSession
			// is not authority."
			SyncDeviceRegistrationEntity? approver = await db.SyncDeviceRegistrations.AsNoTracking()
				.SingleOrDefaultAsync(d => d.AccountId == challenge.AccountId && d.Id == approverId, ct);

			if (approver is not null && Base64Url.TryDecode(approval.Signature, out byte[] approvalSignature))
			{
				try
				{
					proofVerifier.VerifyDeviceApproval(binding, approver.PublicKeySpki, approvalSignature);
					hasValidApproval = true;
				}
				catch (DeviceProofInvalidException)
				{
					hasValidApproval = false;
				}
			}
		}

		// Re-decides and mutates under an account row lock - the authoritative, commit-time check.
		await trustService.TryTrustPendingDeviceAsync(db, challenge.AccountId, challenge.DeviceRegistrationId, approverId, hasValidApproval, now, ct);

		long trustVersion = await db.SyncDeviceRegistrations.AsNoTracking()
			.Where(d => d.AccountId == challenge.AccountId && d.Id == challenge.DeviceRegistrationId)
			.Select(d => d.TrustVersion)
			.SingleAsync(ct);

		SyncLocalInstanceEntity? instance = await db.SyncLocalInstances
			.FirstOrDefaultAsync(i => i.DeviceRegistrationId == challenge.DeviceRegistrationId && i.Id == localInstanceId, ct);
		if (instance is null)
		{
			db.SyncLocalInstances.Add(new SyncLocalInstanceEntity(localInstanceId, challenge.DeviceRegistrationId, now));
		}

		NewSession newSession = await refreshTokenService.CreateSessionAsync(
			db, challenge.AccountId, challenge.DeviceRegistrationId, localInstanceId, trustVersion, now, ct);

		await db.SaveChangesAsync(ct);
		await tx.CommitAsync(ct);

		SessionIdentity identity = new(legacyUserId, challenge.AccountId, newSession.SessionId, challenge.DeviceRegistrationId, localInstanceId, newSession.TrustVersion);
		return tokenIssuer.IssueTokens(identity, newSession.RawRefreshToken, newSession.RefreshTokenExpiresAtUtc, now);
	}
}
