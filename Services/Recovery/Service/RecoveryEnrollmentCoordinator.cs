using System.Globalization;
using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Crypto;
using Services.Recovery.Interface;
using Services.Recovery.Models;
using Services.Sessions;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using Services.Sessions.Service;
using WireContracts = Services.Sync.Contracts;

namespace Services.Recovery.Service;

/// <summary>See <see cref="IRecoveryEnrollmentCoordinator"/>. Call ordering and transaction boundary only - every decision belongs to one of the composed services, exactly like <see cref="Services.Sessions.Service.DeviceEnrollmentCoordinator"/>.</summary>
public sealed class RecoveryEnrollmentCoordinator(
	TimeProvider timeProvider,
	ISyncAccountProvisioningService accountProvisioning,
	IRecoveryEnvelopeService envelopeService,
	IDeviceChallengeService challengeService,
	IDeviceProofVerifier proofVerifier,
	IDeviceTrustService trustService,
	IRefreshTokenService refreshTokenService,
	ISessionTokenIssuer tokenIssuer) : IRecoveryEnrollmentCoordinator
{
	public async Task<WireContracts.RecoveryBootstrapChallenge> RequestRecoveryChallengeAsync(
		AppDbContext db, int legacyUserId, string deviceRegistrationId, string keyId, byte[] spki,
		string localInstanceId, CancellationToken ct = default)
	{
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;
		SyncAccountEntity account = await accountProvisioning.GetOrCreateActiveAccountAsync(db, legacyUserId, now, ct);

		// The barrier is enforced BEFORE any sealed material leaves the server: the envelope's plaintext
		// is the account's author private keys, so handing it to a blocked device and only refusing the
		// session later would already have released the old context's authorship material. Re-checked
		// under the commit locks at register (TryTrustPendingDeviceAsync) for revocation races.
		if (!await trustService.IsRecoverySufficientAsync(db, account.Id, deviceRegistrationId, ct))
		{
			throw new DeviceApprovalRequiredException("Another currently trusted device must approve this registration.");
		}

		IReadOnlyList<RecoveryCandidate> candidates = await envelopeService.GetRecoveryCandidatesAsync(db, account.Id, ct);
		if (candidates.Count == 0)
		{
			throw new RecoveryEnvelopeInvalidException("No recovery envelope exists for this account context.");
		}

		WireContracts.DeviceChallenge challenge = await challengeService.IssueEnrollmentChallengeAsync(
			db, account.Id, deviceRegistrationId, keyId, spki, localInstanceId, ChallengePurpose.Recovery, ct);

		// The envelope is a one-time snapshot; author keys created after setup live in the escrow, sealed
		// to the same verification key, so a recovering device restores authorship for both.
		IReadOnlyList<WireContracts.AuthorKeyEscrowEntry> escrows = await envelopeService.GetAuthorKeyEscrowsAsync(db, account.Id, ct);

		return new WireContracts.RecoveryBootstrapChallenge(challenge, candidates.Select(c => c.Package).ToList(), escrows);
	}

	public async Task<WireContracts.SessionTokens> RegisterWithRecoveryAsync(
		AppDbContext db, int legacyUserId, WireContracts.RecoveryRegisterRequest request, CancellationToken ct = default)
	{
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;

		await using var tx = await db.Database.BeginTransactionAsync(ct);

		SyncDeviceChallengeEntity challenge = await challengeService.ConsumeAsync(db, request.Proof.ChallengeId, ct);
		if (challenge.Purpose != ChallengePurpose.Recovery)
		{
			throw new ChallengeInvalidException("Challenge purpose is not valid for recovery registration.");
		}
		// Both proofs must be over the SAME pending request; otherwise a recovery proof harvested for one
		// challenge could authorize a different device's registration.
		if (!string.Equals(request.RecoveryProof.ChallengeId, request.Proof.ChallengeId, StringComparison.Ordinal))
		{
			throw new DeviceProofInvalidException("Device proof and recovery proof are not bound to the same challenge.");
		}
		if (challenge.LocalInstanceId is null || !string.Equals(challenge.LocalInstanceId, request.LocalInstanceId, StringComparison.Ordinal))
		{
			throw new ChallengeInvalidException("Challenge was not issued for this local instance.");
		}

		SyncAccountEntity account = await db.SyncAccounts.SingleAsync(a => a.Id == challenge.AccountId, ct);
		if (account.LegacyUserId != legacyUserId || account.EndedAtUtc is not null)
		{
			throw new SessionNotTrustedException("Challenge does not belong to the authenticated bootstrap identity's current context.");
		}

		DeviceProofBinding binding = new(
			challenge.Id, challenge.Purpose.ToString(), challenge.AccountId, challenge.DeviceRegistrationId,
			challenge.KeyId, challenge.PublicKeySpki, challenge.Nonce, RfcTimestamp.Format(challenge.ExpiresAtUtc));

		if (!Base64Url.TryDecode(request.Proof.Signature, out byte[] proofSignature)
			|| !Base64Url.TryDecode(request.RecoveryProof.Signature, out byte[] recoverySignature))
		{
			throw new DeviceProofInvalidException("Signature is not canonical base64url.");
		}
		proofVerifier.VerifyDeviceProof(binding, challenge.PublicKeySpki, proofSignature);

		IReadOnlyList<RecoveryCandidate> candidates = await envelopeService.GetRecoveryCandidatesAsync(db, challenge.AccountId, ct);
		RecoveryCandidate? candidate = candidates.FirstOrDefault(
			c => string.Equals(c.Version.ToString(CultureInfo.InvariantCulture), request.RecoveryVersion, StringComparison.Ordinal));
		if (candidate is null)
		{
			throw new DeviceProofInvalidException("Recovery version is not currently accepted for this account context.");
		}

		// The one place secret knowledge is proven to the server: a signature by the key that only an
		// opened envelope contains, over this exact challenge.
		proofVerifier.VerifyRecoveryProof(binding, candidate.VerificationSpki, recoverySignature);

		await trustService.TryTrustPendingDeviceAsync(
			db, challenge.AccountId, challenge.DeviceRegistrationId, approval: null, recoveryProofVerified: true, now, ct);

		long trustVersion = await db.SyncDeviceRegistrations.AsNoTracking()
			.Where(d => d.AccountId == challenge.AccountId && d.Id == challenge.DeviceRegistrationId)
			.Select(d => d.TrustVersion)
			.SingleAsync(ct);

		await LocalInstanceRegistrar.EnsureRecordedAsync(db, challenge.AccountId, challenge.DeviceRegistrationId, request.LocalInstanceId, now, ct);

		NewSession newSession = await refreshTokenService.CreateSessionAsync(
			db, challenge.AccountId, challenge.DeviceRegistrationId, request.LocalInstanceId, trustVersion, now, ct);

		await db.SaveChangesAsync(ct);
		await tx.CommitAsync(ct);

		SessionIdentity identity = new(
			legacyUserId, challenge.AccountId, newSession.SessionId, challenge.DeviceRegistrationId, request.LocalInstanceId, newSession.TrustVersion);
		return tokenIssuer.IssueTokens(identity, newSession.RawRefreshToken, newSession.RefreshTokenExpiresAtUtc, now);
	}
}
