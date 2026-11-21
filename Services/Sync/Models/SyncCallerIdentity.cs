using System.Security.Claims;
using Database.Context;
using Microsoft.EntityFrameworkCore;
using Services.Sessions;
using Services.Sync.Contracts;

namespace Services.Sync.Models;

/// <summary>
/// The caller as the SERVER knows it: account context, device registration, local instance and device
/// session, all read from the authenticated token's own claims and verified against the rows they will
/// be used with. Nothing here is ever taken from a request body - a supplied
/// <see cref="SessionContext"/> is only ever CHECKED against this ("Server derives the device/account
/// from authenticated session and checks supplied context").
/// </summary>
public sealed record SyncCallerIdentity(
	string AccountContextId,
	string DeviceRegistrationId,
	string LocalInstanceId,
	string DeviceSessionId,
	long TrustVersion)
{
	public DeviceContext Device => new(AccountContextId, DeviceRegistrationId, LocalInstanceId);

	/// <summary>
	/// Resolves and verifies the caller, or null when the token carries no complete sync identity or the
	/// instance/registration/session do not really belong together. The claims are server-issued, but
	/// these are the rows every foreign key below will point at, so the ownership is re-read rather than
	/// assumed - the same check <see cref="Services.Messaging.Service.MessageAcceptanceService"/> makes.
	/// </summary>
	public static async Task<SyncCallerIdentity?> ResolveAsync(AppDbContext db, ClaimsPrincipal caller, CancellationToken ct)
	{
		string? accountId = caller.FindFirstValue(SessionClaimTypes.AccountContextId);
		string? deviceId = caller.FindFirstValue(SessionClaimTypes.DeviceRegistrationId);
		string? instanceId = caller.FindFirstValue(SessionClaimTypes.LocalInstanceId);
		string? sessionId = caller.FindFirstValue(SessionClaimTypes.SessionId);
		string? trustVersionClaim = caller.FindFirstValue(SessionClaimTypes.TrustVersion);

		if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(instanceId)
			|| string.IsNullOrEmpty(sessionId) || !long.TryParse(trustVersionClaim, out long trustVersion))
		{
			return null;
		}

		bool owned = await db.SyncDeviceSessions.AsNoTracking()
			.AnyAsync(s => s.Id == sessionId
				&& s.AccountId == accountId
				&& s.DeviceRegistrationId == deviceId
				&& s.LocalInstanceId == instanceId
				&& s.EndedAtUtc == null, ct);

		return owned ? new SyncCallerIdentity(accountId, deviceId, instanceId, sessionId, trustVersion) : null;
	}

	/// <summary>Whether a client-supplied session context names exactly this caller - never used to widen it.</summary>
	public bool Matches(SessionContext? supplied) =>
		supplied?.Device is not null
		&& string.Equals(supplied.Device.AccountContextId, AccountContextId, StringComparison.Ordinal)
		&& string.Equals(supplied.Device.DeviceRegistrationId, DeviceRegistrationId, StringComparison.Ordinal)
		&& string.Equals(supplied.Device.LocalInstanceId, LocalInstanceId, StringComparison.Ordinal)
		&& string.Equals(supplied.SessionId, DeviceSessionId, StringComparison.Ordinal)
		&& string.Equals(supplied.TrustVersion, TrustVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
