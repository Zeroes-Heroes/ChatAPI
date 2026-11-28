using System.Security.Claims;
using Database.Context;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Services.Sessions.Interface;
using Services.Sessions.Models;

namespace Services.Sessions.Service;

/// <summary>
/// See <see cref="ISessionTrustChecker"/>. Reads one row per table with no tracking - this runs on
/// every HTTP request and hub invocation, so it stays a minimal, index-backed lookup, never a larger
/// query. Signature/issuer/audience are the JwtBearer handshake/HTTP auth pipeline's job (it already
/// rejects a tampered or wrong-issuer/audience token before this ever runs) - but <c>exp</c> is
/// re-checked HERE too, not only trusted from that pipeline: for a SignalR hub, authentication/
/// authorization only runs once, at connect, so a token that was valid at connect but has since
/// expired is never re-validated by the JwtBearer handler for any later frame on that same
/// long-lived connection - only this checker, invoked by <see cref="Hubs.Filters.SessionGuardHubFilter"/>
/// on every single invocation, ever looks at it again. Also binds the token to the authoritative
/// server-side identity it claims: the legacy subject (<see cref="ClaimTypes.NameIdentifier"/>) must
/// still match the account's own <see cref="Database.Entities.Sync.SyncAccountEntity.LegacyUserId"/>,
/// and the LocalInstanceId claim must still match the session's own bound
/// <see cref="Database.Entities.Sync.SyncDeviceSessionEntity.LocalInstanceId"/> - a token whose claims
/// no longer correspond to the server's own records for that session/account must fail the same as a
/// revoked one, not merely one with a stale TrustVersion.
/// </summary>
public sealed class SessionTrustChecker(AppDbContext db, TimeProvider timeProvider) : ISessionTrustChecker
{
	public async Task<TrustCheckResult> CheckAndLockDeviceForMutationAsync(ClaimsPrincipal principal, CancellationToken ct = default)
	{
		string? accountId = principal.FindFirstValue(SessionClaimTypes.AccountContextId);
		string? deviceRegistrationId = principal.FindFirstValue(SessionClaimTypes.DeviceRegistrationId);
		string? sessionId = principal.FindFirstValue(SessionClaimTypes.SessionId);
		if (!string.IsNullOrEmpty(accountId) && !string.IsNullOrEmpty(deviceRegistrationId))
		{
			// Participates in the caller's own ambient transaction (Database.CurrentTransaction) - see
			// this method's own interface-level doc comment for why this is the real serialization
			// point against a concurrent DeviceTrustService.RevokeDeviceAsync for the same device, not
			// merely "read again right before the mutation" (which would still race).
			await db.SyncDeviceRegistrations
				.FromSqlInterpolated($"""SELECT * FROM "SyncDeviceRegistrations" WHERE "AccountId" = {accountId} AND "Id" = {deviceRegistrationId} FOR UPDATE""")
				.AsNoTracking()
				.ToListAsync(ct);
		}
		if (!string.IsNullOrEmpty(accountId) && !string.IsNullOrEmpty(sessionId))
		{
			// Locks the caller's own SESSION row too, after the device lock above - same account->device->
			// session ordering RevokeDeviceAsync/RefreshTokenService.RotateAsync already use (see those
			// methods' own doc comments), so this can never deadlock against either. A device-only lock does
			// NOT serialize against RefreshTokenService.RotateAsync's reuse-detection path, which ends a
			// session WITHOUT ever touching the device row (see that method's own doc comment) - without
			// this second lock, a mutation here could still observe a stale "session not yet ended" read
			// racing a concurrent reuse-detection commit for the exact session this principal's token claims.
			await db.SyncDeviceSessions
				.FromSqlInterpolated($"""SELECT * FROM "SyncDeviceSessions" WHERE "AccountId" = {accountId} AND "Id" = {sessionId} FOR UPDATE""")
				.AsNoTracking()
				.ToListAsync(ct);
		}
		// Absence of a matching row (malformed/missing claims, or a device/session that genuinely does not
		// exist) is uniformly handled by CheckAsync's own missing-claims/no-matching-row branches below.
		return await CheckAsync(principal, ct);
	}

	public async Task<TrustCheckResult> CheckAsync(ClaimsPrincipal principal, CancellationToken ct = default)
	{
		string? purpose = principal.FindFirstValue(SessionClaimTypes.TokenPurpose);
		if (purpose != SessionClaimTypes.AppPurpose)
		{
			return TrustCheckResult.NotTrusted("Token is not an app-purpose session token.");
		}

		string? legacySubjectClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier);
		string? accountId = principal.FindFirstValue(SessionClaimTypes.AccountContextId);
		string? sessionId = principal.FindFirstValue(SessionClaimTypes.SessionId);
		string? deviceRegistrationId = principal.FindFirstValue(SessionClaimTypes.DeviceRegistrationId);
		string? localInstanceIdClaim = principal.FindFirstValue(SessionClaimTypes.LocalInstanceId);
		string? trustVersionClaim = principal.FindFirstValue(SessionClaimTypes.TrustVersion);
		string? expClaim = principal.FindFirstValue(JwtRegisteredClaimNames.Exp);

		if (string.IsNullOrEmpty(legacySubjectClaim) || string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(sessionId)
			|| string.IsNullOrEmpty(deviceRegistrationId) || string.IsNullOrEmpty(localInstanceIdClaim)
			|| !long.TryParse(trustVersionClaim, out long tokenTrustVersion)
			|| !int.TryParse(legacySubjectClaim, out int legacySubjectUserId)
			|| !long.TryParse(expClaim, out long expSeconds))
		{
			return TrustCheckResult.NotTrusted("Token is missing required session claims.");
		}

		// Re-validated every single invocation (not only once, at handshake/connect) - see this class's
		// own doc comment for why a hub connection cannot rely on the JwtBearer pipeline for this.
		DateTimeOffset expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
		if (expiresAtUtc <= timeProvider.GetUtcNow())
		{
			return TrustCheckResult.NotTrusted("Access token has expired.");
		}

		var row = await db.SyncDeviceSessions
			.Where(s => s.Id == sessionId && s.AccountId == accountId && s.DeviceRegistrationId == deviceRegistrationId)
			.Select(s => new
			{
				s.EndedAtUtc,
				s.LocalInstanceId,
				Device = db.SyncDeviceRegistrations
					.Where(d => d.AccountId == accountId && d.Id == deviceRegistrationId)
					.Select(d => new { d.TrustState, d.TrustVersion })
					.FirstOrDefault(),
				Account = db.SyncAccounts
					.Where(a => a.Id == accountId)
					.Select(a => new { a.EndedAtUtc, a.LegacyUserId })
					.FirstOrDefault(),
			})
			.AsNoTracking()
			.FirstOrDefaultAsync(ct);

		if (row is null)
		{
			return TrustCheckResult.NotTrusted("No matching session for this account/device.");
		}
		if (row.EndedAtUtc is not null)
		{
			return TrustCheckResult.NotTrusted("Session has ended.");
		}
		if (row.LocalInstanceId != localInstanceIdClaim)
		{
			return TrustCheckResult.NotTrusted("Token's local-instance binding no longer matches the session's bound local instance.");
		}
		if (row.Account is null || row.Account.EndedAtUtc is not null)
		{
			return TrustCheckResult.NotTrusted("Account context has ended.");
		}
		if (row.Account.LegacyUserId != legacySubjectUserId)
		{
			return TrustCheckResult.NotTrusted("Token's legacy subject no longer corresponds to this account.");
		}
		if (row.Device is null || row.Device.TrustState != DeviceTrustState.Trusted)
		{
			return TrustCheckResult.NotTrusted("Device is not currently trusted.");
		}
		if (row.Device.TrustVersion != tokenTrustVersion)
		{
			return TrustCheckResult.NotTrusted("Token trust version no longer matches the device's current trust epoch.");
		}

		return TrustCheckResult.Trusted;
	}
}
