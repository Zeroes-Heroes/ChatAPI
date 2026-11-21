namespace Services.Sessions.Models;

/// <summary>Everything an "app" access token embeds as claims (see <see cref="SessionClaimTypes"/>), gathered from already-authorized server state - never from caller-supplied request fields.</summary>
public sealed record SessionIdentity(
	int LegacyUserId,
	string AccountContextId,
	string SessionId,
	string DeviceRegistrationId,
	string LocalInstanceId,
	long TrustVersion);
