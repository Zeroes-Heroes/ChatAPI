using System.Security.Cryptography;
using System.Text.Json;
using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using WireContracts = Services.Sync.Contracts;

namespace Services.Sessions.Service;

/// <summary>
/// See <see cref="IRefreshTokenService"/>. <see cref="RotateAsync"/> is the heart of the module's
/// replay/reuse/concurrency invariants - every branch is documented inline against the exact
/// docs/sync-contract-v1.md sentence it implements.
/// </summary>
public sealed class RefreshTokenService(
	TimeProvider timeProvider,
	ISessionTokenIssuer tokenIssuer,
	IReplayPayloadProtector replayPayloadProtector,
	ISessionRevocationNotifier revocationNotifier) : IRefreshTokenService
{
	/// <summary>Bounded replay window: an identical retry of the same (SessionId, RefreshOperationId) within this window replays the cached result; see SyncRefreshTokenRotationEntity's own doc comment for the documented, deliberate fail-closed behavior once this window passes.</summary>
	private static readonly TimeSpan ReplayWindow = TimeSpan.FromMinutes(5);
	private const int RawTokenLength = 32;
	private const int SessionIdLength = 20;

	public Task<NewSession> CreateSessionAsync(
		AppDbContext db, string accountId, string deviceRegistrationId, string localInstanceId, long trustVersion,
		DateTime now, CancellationToken ct = default)
	{
		string sessionId = Services.Crypto.Base64Url.Encode(RandomNumberGenerator.GetBytes(SessionIdLength));
		string rawToken = GenerateRawToken();
		byte[] hash = HashToken(rawToken);
		DateTime refreshExpiresAtUtc = now.AddDays(ISessionTokenIssuer.RefreshTokenLifetimeDays);

		SyncDeviceSessionEntity session = new(sessionId, accountId, deviceRegistrationId, localInstanceId, now, trustVersion, hash, refreshExpiresAtUtc);
		db.SyncDeviceSessions.Add(session);

		return Task.FromResult(new NewSession(sessionId, trustVersion, rawToken, refreshExpiresAtUtc));
	}

	public async Task<WireContracts.RefreshSessionResult> RotateAsync(
		AppDbContext db, string accountId, string sessionId, string refreshOperationId, string suppliedRawRefreshToken,
		CancellationToken ct = default)
	{
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;
		byte[] suppliedHash = HashToken(suppliedRawRefreshToken);

		await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);

		// Row-lock the session for the whole decision: this is what makes "two concurrent requests
		// for the identical (sessionId, refreshOperationId) retry" and "refresh racing a concurrent
		// revoke of the same session" resolve deterministically instead of racing on a read-then-write
		// outside the database. The second concurrent transaction blocks here until the first commits
		// or rolls back, then re-evaluates from the now-current row.
		SyncDeviceSessionEntity? session = await db.SyncDeviceSessions
			.FromSqlInterpolated($"""SELECT * FROM "SyncDeviceSessions" WHERE "Id" = {sessionId} AND "AccountId" = {accountId} FOR UPDATE""")
			.AsNoTracking()
			.SingleOrDefaultAsync(ct);

		if (session is null || session.EndedAtUtc is not null)
		{
			await tx.CommitAsync(ct);
			return new WireContracts.RefreshSessionResult(WireContracts.RefreshOutcome.ReauthenticationRequired, null, null);
		}

		// Idempotency boundary: (SessionId, RefreshOperationId) is this table's primary key - a prior
		// successful rotation for this exact operation is found here, never re-derived from the
		// session's current hash alone. "Identical retries return the same rotation result within a
		// bounded replay window."
		SyncRefreshTokenRotationEntity? existingRotation = await db.SyncRefreshTokenRotations.AsNoTracking()
			.FirstOrDefaultAsync(r => r.SessionId == sessionId && r.RefreshOperationId == refreshOperationId, ct);

		if (existingRotation is not null)
		{
			await tx.CommitAsync(ct);
			return ReplayExistingRotation(accountId, sessionId, refreshOperationId, now, existingRotation);
		}

		SyncDeviceRegistrationEntity device = await db.SyncDeviceRegistrations.AsNoTracking()
			.SingleAsync(d => d.AccountId == accountId && d.Id == session.DeviceRegistrationId, ct);
		SyncAccountEntity account = await db.SyncAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId, ct);

		// "Validate trust at commit" / "revoked session fails closed even if JWT not expired": checked
		// here, under the same row lock, immediately before any mutation - not earlier in the request
		// (e.g. only at proof verification time), so a revoke that lands between proof verification
		// and this point is never missed.
		if (account.EndedAtUtc is not null || device.TrustState != DeviceTrustState.Trusted || device.TrustVersion != session.TrustVersionAtIssuance)
		{
			await tx.CommitAsync(ct);
			return new WireContracts.RefreshSessionResult(WireContracts.RefreshOutcome.DeviceRevoked, null, null);
		}

		// Constant-time comparison against the single expected current value - the decisive accept
		// check, never a SQL/`==` equality alone for this specific comparison.
		bool isCurrentToken = session.CurrentRefreshTokenHash is not null
			&& CryptographicOperations.FixedTimeEquals(suppliedHash, session.CurrentRefreshTokenHash);

		if (!isCurrentToken)
		{
			bool everValidForThisSession = await db.SyncRefreshTokenRotations.AsNoTracking().AnyAsync(
				r => r.SessionId == sessionId && (r.OldRefreshTokenHash == suppliedHash || r.NewRefreshTokenHash == suppliedHash), ct);

			if (everValidForThisSession)
			{
				// "Reuse under a different operation ID revokes the family" (RFC 9700 refresh-token
				// rotation reuse detection): the presented secret was valid at some point in this
				// session's own history but is not the current one - someone (attacker or a genuine
				// duplicate-device race) is replaying a superseded secret. Revoke the whole session,
				// never guess at "which request is legitimate" by id alone.
				await db.Database.ExecuteSqlInterpolatedAsync(
					$"""UPDATE "SyncDeviceSessions" SET "EndedAtUtc" = {now}, "CurrentRefreshTokenHash" = NULL, "RefreshTokenExpiresAtUtc" = NULL WHERE "Id" = {sessionId}""", ct);
				await tx.CommitAsync(ct);
				await revocationNotifier.NotifySessionRevokedAsync(sessionId, WireContracts.SessionControlReason.Revoked, ct);
				return new WireContracts.RefreshSessionResult(WireContracts.RefreshOutcome.ReplayDetected, null, null);
			}

			// Never valid for this session at all - wrong/forged/foreign token. Fail closed without
			// revealing which of "unknown" vs "wrong value" applies.
			await tx.CommitAsync(ct);
			return new WireContracts.RefreshSessionResult(WireContracts.RefreshOutcome.ReauthenticationRequired, null, null);
		}

		string newRawToken = GenerateRawToken();
		byte[] newHash = HashToken(newRawToken);
		DateTime newRefreshExpiresAtUtc = now.AddDays(ISessionTokenIssuer.RefreshTokenLifetimeDays);

		int rotated = await db.Database.ExecuteSqlInterpolatedAsync(
			$"""
			UPDATE "SyncDeviceSessions" SET "CurrentRefreshTokenHash" = {newHash}, "RefreshTokenExpiresAtUtc" = {newRefreshExpiresAtUtc},
				"LastRotationOperationId" = {refreshOperationId}, "LastRotationAtUtc" = {now}
			WHERE "Id" = {sessionId} AND "CurrentRefreshTokenHash" = {session.CurrentRefreshTokenHash}
			""", ct);

		if (rotated != 1)
		{
			// The FOR UPDATE lock should make this unreachable; fail closed rather than silently
			// proceeding on an assumption that no longer holds.
			await tx.RollbackAsync(ct);
			return new WireContracts.RefreshSessionResult(WireContracts.RefreshOutcome.Retryable, null, 250);
		}

		SessionIdentity identity = new(account.LegacyUserId, accountId, sessionId, session.DeviceRegistrationId, session.LocalInstanceId, device.TrustVersion);
		WireContracts.SessionTokens tokens = tokenIssuer.IssueTokens(identity, newRawToken, newRefreshExpiresAtUtc, now);

		byte[] plaintextPayload = JsonSerializer.SerializeToUtf8Bytes(tokens, WireContracts.SyncProtocol.CreateJsonOptions());
		Models.ProtectedReplayPayload protectedPayload = replayPayloadProtector.Protect(
			plaintextPayload, new Models.ReplayProtectionContext(accountId, sessionId, refreshOperationId));

		SyncRefreshTokenRotationEntity rotation = new(
			sessionId, refreshOperationId, session.CurrentRefreshTokenHash!, newHash, now, now.Add(ReplayWindow), protectedPayload.Ciphertext)
		{
			ProtectedReplayNonce = protectedPayload.Nonce,
			ProtectedReplayTag = protectedPayload.Tag,
			ProtectedReplayMasterKeyVersion = protectedPayload.MasterKeyVersion,
		};
		db.SyncRefreshTokenRotations.Add(rotation);
		await db.SaveChangesAsync(ct);
		await tx.CommitAsync(ct);

		return new WireContracts.RefreshSessionResult(WireContracts.RefreshOutcome.Rotated, tokens, null);
	}

	private WireContracts.RefreshSessionResult ReplayExistingRotation(
		string accountId, string sessionId, string refreshOperationId, DateTime now, SyncRefreshTokenRotationEntity existingRotation)
	{
		if (now > existingRotation.ReplayExpiresAtUtc || existingRotation.ProtectedReplayPayload is null)
		{
			// Past the bounded window (or already purged): this rotation genuinely already committed,
			// but the raw tokens it produced can no longer be safely reconstructed and were never kept
			// in the clear - see SyncRefreshTokenRotationEntity's doc comment for why ReauthenticationRequired
			// (fail closed), not a guess, is the documented behavior here.
			return new WireContracts.RefreshSessionResult(WireContracts.RefreshOutcome.ReauthenticationRequired, null, null);
		}

		byte[] plaintext = replayPayloadProtector.Unprotect(
			new Models.ProtectedReplayPayload(
				existingRotation.ProtectedReplayMasterKeyVersion!, existingRotation.ProtectedReplayNonce!,
				existingRotation.ProtectedReplayPayload, existingRotation.ProtectedReplayTag!),
			new Models.ReplayProtectionContext(accountId, sessionId, refreshOperationId));

		WireContracts.SessionTokens tokens = JsonSerializer.Deserialize<WireContracts.SessionTokens>(plaintext, WireContracts.SyncProtocol.CreateJsonOptions())
			?? throw new InvalidOperationException("Replay payload deserialized to null.");
		return new WireContracts.RefreshSessionResult(WireContracts.RefreshOutcome.Rotated, tokens, null);
	}

	private static string GenerateRawToken() => Services.Crypto.Base64Url.Encode(RandomNumberGenerator.GetBytes(RawTokenLength));

	private static byte[] HashToken(string rawToken) => SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(rawToken));
}
