using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Crypto;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using WireContracts = Services.Sync.Contracts;

namespace Services.Sessions.Service;

public sealed class SessionRefreshCoordinator(
	IDeviceChallengeService challengeService,
	IDeviceProofVerifier proofVerifier,
	IRefreshTokenService refreshTokenService) : ISessionRefreshCoordinator
{
	public async Task<WireContracts.DeviceChallenge> RequestSessionBindingChallengeAsync(
		AppDbContext db, string sessionId, string refreshOperationId, CancellationToken ct = default)
	{
		SyncDeviceSessionEntity session = await db.SyncDeviceSessions.AsNoTracking()
			.FirstOrDefaultAsync(s => s.Id == sessionId && s.EndedAtUtc == null, ct)
			?? throw new SessionNotTrustedException("Unknown or ended session.");

		return await challengeService.IssueSessionBindingChallengeAsync(
			db, session.AccountId, session.DeviceRegistrationId, sessionId, refreshOperationId, ct);
	}

	public async Task<WireContracts.RefreshSessionResult> RefreshAsync(
		AppDbContext db, WireContracts.RefreshSessionRequest request, CancellationToken ct = default)
	{
		SyncDeviceSessionEntity? session = await db.SyncDeviceSessions.AsNoTracking()
			.FirstOrDefaultAsync(s => s.Id == request.SessionId, ct);
		if (session is null)
		{
			return Rejected();
		}

		SyncDeviceChallengeEntity challenge;
		try
		{
			challenge = await challengeService.ConsumeAsync(db, request.Proof.ChallengeId, ct);
		}
		catch (ChallengeInvalidException)
		{
			return Rejected();
		}

		// Every field the challenge was server-bound to at issuance must match this exact request -
		// "additionally bound server-side to SessionId/RefreshOperationId" (docs/sync-contract-v1.md).
		bool bindingMatches =
			challenge.Purpose == ChallengePurpose.SessionBinding
			&& challenge.SessionId == request.SessionId
			&& challenge.RefreshOperationId == request.RefreshOperationId
			&& challenge.AccountId == session.AccountId
			&& challenge.DeviceRegistrationId == session.DeviceRegistrationId;
		if (!bindingMatches)
		{
			return Rejected();
		}

		DeviceProofBinding binding = new(
			challenge.Id, challenge.Purpose.ToString(), challenge.AccountId, challenge.DeviceRegistrationId,
			challenge.KeyId, challenge.PublicKeySpki, challenge.Nonce, RfcTimestamp.Format(challenge.ExpiresAtUtc));

		if (!Base64Url.TryDecode(request.Proof.Signature, out byte[] signature))
		{
			return Rejected();
		}

		try
		{
			proofVerifier.VerifyDeviceProof(binding, challenge.PublicKeySpki, signature);
		}
		catch (DeviceProofInvalidException)
		{
			return Rejected();
		}

		return await refreshTokenService.RotateAsync(db, session.AccountId, request.SessionId, request.RefreshOperationId, request.RefreshToken, ct);
	}

	/// <summary>Uniform fail-closed response for every pre-rotation rejection (unknown session, invalid/expired/mismatched challenge, bad proof) - deliberately indistinguishable from each other, same as <see cref="ChallengeInvalidException"/>'s own doc comment.</summary>
	private static WireContracts.RefreshSessionResult Rejected() =>
		new(WireContracts.RefreshOutcome.ReauthenticationRequired, null, null);
}
