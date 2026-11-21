namespace Services.Sessions;

/// <summary>
/// Claim types embedded in the two distinct JWT shapes this stage distinguishes (plan/05-device-sessions.md).
/// A "bootstrap" token (minted by the existing <see cref="Services.Token.Service.TokenService"/> after
/// legacy SMS/PIN login) carries only <see cref="TokenPurpose"/>=<see cref="BootstrapPurpose"/> plus the
/// legacy <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/> - it authorizes only the two
/// device-enrollment endpoints (policy "Bootstrap"), never general API/hub access. An "app" token
/// (minted by <see cref="Service.SessionTokenIssuer"/> after a verified device proof) carries
/// <see cref="TokenPurpose"/>=<see cref="AppPurpose"/> plus the full device/session/trust claim set and
/// is the only token shape the default authorization policy accepts - see
/// <see cref="Service.SessionTrustChecker"/>. This is what stops the legacy credential-only token from
/// remaining an unrestricted-access bypass after this stage (docs/sync-contract-v1.md's session model).
/// </summary>
public static class SessionClaimTypes
{
	private const string Prefix = "http://schemas.turbo.com/claims/session/";

	public const string TokenPurpose = Prefix + "purpose";
	public const string AccountContextId = Prefix + "accountContextId";
	public const string SessionId = Prefix + "sessionId";
	public const string DeviceRegistrationId = Prefix + "deviceRegistrationId";
	public const string LocalInstanceId = Prefix + "localInstanceId";
	public const string TrustVersion = Prefix + "trustVersion";

	public const string BootstrapPurpose = "bootstrap";
	public const string AppPurpose = "app";
}
