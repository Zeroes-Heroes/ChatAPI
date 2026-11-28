using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Services.Crypto;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using WireContracts = Services.Sync.Contracts;

namespace Services.Sessions.Service;

/// <summary>See <see cref="IDeviceApprovalService"/>.</summary>
public sealed class DeviceApprovalService(IDeviceProofVerifier proofVerifier) : IDeviceApprovalService
{
	/// <summary>Bounded response, same spirit as the wire contract's own page caps - an account's pending-approval list is a UI list, never an unbounded enumeration.</summary>
	private const int MaxPendingRequests = 25;

	private const int ApprovalSignatureLength = 64;

	public async Task<IReadOnlyList<WireContracts.PendingApprovalRequest>> ListPendingAsync(
		AppDbContext db, string accountId, DateTime now, CancellationToken ct = default)
	{
		List<SyncDeviceChallengeEntity> pending = await db.SyncDeviceChallenges.AsNoTracking()
			.Where(c => c.AccountId == accountId
				&& c.Purpose == ChallengePurpose.DeviceApproval
				&& c.ConsumedAtUtc == null
				&& c.ExpiresAtUtc > now
				// Already-approved requests are waiting for the requesting device to finish registering,
				// not for another approval - showing them again would invite a second, pointless tap that
				// this service would then have to refuse.
				&& !db.SyncDeviceApprovalGrants.Any(g => g.ChallengeId == c.Id))
			.OrderByDescending(c => c.CreatedAtUtc)
			.Take(MaxPendingRequests)
			.ToListAsync(ct);

		return pending.ConvertAll(c => new WireContracts.PendingApprovalRequest(
			c.Id,
			(WireContracts.ChallengePurpose)c.Purpose,
			c.AccountId,
			c.DeviceRegistrationId,
			new WireContracts.DevicePublicKey(c.KeyId, Base64Url.Encode(c.PublicKeySpki)),
			// Non-null for every approval-purpose challenge - enforced by
			// CK_SyncDeviceChallenges_LocalInstanceMatchesPurpose, not merely by convention.
			c.LocalInstanceId!,
			Base64Url.Encode(c.Nonce),
			RfcTimestamp.Format(c.CreatedAtUtc),
			RfcTimestamp.Format(c.ExpiresAtUtc)));
	}

	public async Task<WireContracts.ApproveDeviceResult> ApproveAsync(
		AppDbContext db, string accountId, string approvingDeviceRegistrationId, string approvingSessionId,
		WireContracts.ApproveDeviceRequest request, DateTime now, CancellationToken ct = default)
	{
		if (!Base64Url.TryDecode(request.Signature, out byte[] signature) || signature.Length != ApprovalSignatureLength)
		{
			throw new DeviceProofInvalidException($"Approval signature must be canonical base64url of exactly {ApprovalSignatureLength} bytes (IEEE P1363 R||S).");
		}

		await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);

		// account -> device -> session, the one lock order this codebase uses everywhere (see
		// DeviceTrustService.RevokeDeviceAsync's own doc comment). Taking the account lock here as well
		// means recording a grant genuinely serializes against a concurrent revocation of the approver
		// rather than reading a state that revocation is halfway through changing.
		await db.SyncAccounts
			.FromSqlInterpolated($"""SELECT * FROM "SyncAccounts" WHERE "Id" = {accountId} FOR UPDATE""")
			.AsNoTracking()
			.SingleAsync(ct);

		SyncDeviceChallengeEntity challenge = await db.SyncDeviceChallenges.AsNoTracking()
			.FirstOrDefaultAsync(c => c.Id == request.ChallengeId && c.AccountId == accountId, ct)
			?? throw new ApprovalRequestInvalidException("Unknown request for this account context.");

		if (challenge.Purpose != ChallengePurpose.DeviceApproval)
		{
			throw new ApprovalRequestInvalidException("Request is not awaiting device approval.");
		}
		if (challenge.ConsumedAtUtc is not null || challenge.ExpiresAtUtc <= now)
		{
			throw new ApprovalRequestInvalidException("Request has expired or was already used.");
		}
		if (challenge.DeviceRegistrationId == approvingDeviceRegistrationId)
		{
			// "Another currently trusted device must approve" - a device approving its own pending
			// registration would make that rule vacuous.
			throw new ApprovalRequestInvalidException("A device cannot approve its own registration.");
		}
		if (challenge.LocalInstanceId is null)
		{
			throw new ApprovalRequestInvalidException("Request has no bound local context.");
		}

		SyncDeviceRegistrationEntity? approver = await db.SyncDeviceRegistrations
			.FromSqlInterpolated($"""SELECT * FROM "SyncDeviceRegistrations" WHERE "AccountId" = {accountId} AND "Id" = {approvingDeviceRegistrationId} FOR UPDATE""")
			.AsNoTracking()
			.SingleOrDefaultAsync(ct);
		if (approver is null || approver.TrustState != DeviceTrustState.Trusted)
		{
			throw new SessionNotTrustedException("Approving device is not currently trusted.");
		}

		// A device can stay Trusted while this particular session has independently ended (refresh-token
		// reuse detection ends a session without touching the device row) - the same gap
		// RevokeDeviceAsync re-checks for its acting session, re-checked here for the approver's.
		SyncDeviceSessionEntity? approverSession = await db.SyncDeviceSessions
			.FromSqlInterpolated($"""SELECT * FROM "SyncDeviceSessions" WHERE "Id" = {approvingSessionId} AND "AccountId" = {accountId} FOR UPDATE""")
			.AsNoTracking()
			.SingleOrDefaultAsync(ct);
		if (approverSession is null || approverSession.DeviceRegistrationId != approvingDeviceRegistrationId || approverSession.EndedAtUtc is not null)
		{
			throw new SessionNotTrustedException("Approving session is no longer currently live.");
		}

		// Built from the SERVER's own challenge row, never from anything the approver echoed back, so the
		// signature can only ever verify for the exact pending request/public key the server issued.
		DeviceProofBinding binding = new(
			challenge.Id, challenge.Purpose.ToString(), challenge.AccountId, challenge.DeviceRegistrationId,
			challenge.KeyId, challenge.PublicKeySpki, challenge.Nonce, RfcTimestamp.Format(challenge.ExpiresAtUtc));
		proofVerifier.VerifyDeviceApproval(binding, approver.PublicKeySpki, signature);

		SyncDeviceApprovalGrantEntity? existing = await db.SyncDeviceApprovalGrants.AsNoTracking()
			.FirstOrDefaultAsync(g => g.ChallengeId == challenge.Id, ct);
		if (existing is not null)
		{
			if (existing.ConsumedAtUtc is not null)
			{
				throw new ApprovalRequestInvalidException("Request has expired or was already used.");
			}
			if (existing.ApprovingDeviceRegistrationId != approvingDeviceRegistrationId)
			{
				throw new ApprovalRequestInvalidException("Request has already been approved by another device.");
			}

			await tx.CommitAsync(ct);
			return new WireContracts.ApproveDeviceResult(
				WireContracts.DeviceApprovalOutcome.AlreadyApproved, challenge.Id, challenge.DeviceRegistrationId);
		}

		db.SyncDeviceApprovalGrants.Add(new SyncDeviceApprovalGrantEntity(
			challenge.Id, accountId, challenge.DeviceRegistrationId, challenge.KeyId, challenge.PublicKeySpki,
			challenge.LocalInstanceId, approvingDeviceRegistrationId, approvingSessionId, signature,
			challenge.ExpiresAtUtc, now));

		// Authorship handoff rides the same commit as the grant: approval alone must restore the
		// original author keys, without the recovery secret (MESSAGE_SYNC_ANALYSIS.md:153). Opaque to
		// the server - sealed to the requesting device's own public key.
		if (request.Handoff is not null)
		{
			db.SyncAuthorKeyHandoffs.Add(BuildHandoff(challenge, accountId, request.Handoff, now));
		}

		await db.SaveChangesAsync(ct);
		await tx.CommitAsync(ct);

		return new WireContracts.ApproveDeviceResult(
			WireContracts.DeviceApprovalOutcome.Recorded, challenge.Id, challenge.DeviceRegistrationId);
	}

	public async Task<WireContracts.AuthorKeyHandoffView?> GetPendingHandoffAsync(
		AppDbContext db, string accountId, string deviceRegistrationId, CancellationToken ct = default)
	{
		SyncAuthorKeyHandoffEntity? handoff = await db.SyncAuthorKeyHandoffs.AsNoTracking()
			.Where(h => h.AccountId == accountId && h.PendingDeviceRegistrationId == deviceRegistrationId && h.DeliveredAtUtc == null)
			.OrderByDescending(h => h.CreatedAtUtc)
			.FirstOrDefaultAsync(ct);
		if (handoff is null)
		{
			return null;
		}

		return new WireContracts.AuthorKeyHandoffView(
			handoff.ChallengeId,
			Base64Url.Encode(handoff.EphemeralPublicKeySpki),
			new WireContracts.EncryptedContent(
				Base64Url.Encode(handoff.Nonce), Base64Url.Encode(handoff.Ciphertext), Base64Url.Encode(handoff.Tag)));
	}

	public async Task AcknowledgeHandoffAsync(
		AppDbContext db, string accountId, string deviceRegistrationId, string challengeId, DateTime now, CancellationToken ct = default)
	{
		// Scoped to the acknowledging device, and idempotent: a repeated ack simply affects no row.
		await db.Database.ExecuteSqlInterpolatedAsync(
			$"""
			UPDATE "SyncAuthorKeyHandoffs" SET "DeliveredAtUtc" = {now}
			WHERE "ChallengeId" = {challengeId} AND "AccountId" = {accountId}
			  AND "PendingDeviceRegistrationId" = {deviceRegistrationId} AND "DeliveredAtUtc" IS NULL
			""", ct);
	}

	private static SyncAuthorKeyHandoffEntity BuildHandoff(
		SyncDeviceChallengeEntity challenge, string accountId, WireContracts.AuthorKeyHandoff handoff, DateTime now)
	{
		if (!Base64Url.TryDecode(handoff.EphemeralPublicKeySpki, out byte[] ephemeralSpki) || ephemeralSpki.Length == 0
			|| !Base64Url.TryDecode(handoff.Content.Nonce, out byte[] nonce) || nonce.Length != 12
			|| !Base64Url.TryDecode(handoff.Content.Tag, out byte[] tag) || tag.Length != 16
			|| !Base64Url.TryDecode(handoff.Content.Ciphertext, out byte[] ciphertext) || ciphertext.Length == 0)
		{
			throw new ApprovalRequestInvalidException("Author-key handoff is malformed.");
		}

		return new SyncAuthorKeyHandoffEntity(
			challenge.Id, accountId, challenge.DeviceRegistrationId, ephemeralSpki, nonce, ciphertext, tag, now);
	}
}
