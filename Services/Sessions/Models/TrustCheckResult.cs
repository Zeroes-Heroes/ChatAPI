namespace Services.Sessions.Models;

/// <summary>Outcome of re-validating current trust for an already-authenticated (signature/expiry/issuer/audience valid) "app" access token - see <see cref="Interface.ISessionTrustChecker"/>. <see cref="FailureReason"/> is for server-side diagnostics only; callers must never echo it back to the client (fail closed uniformly, not informatively) - see plan/05-device-sessions.md.</summary>
public sealed record TrustCheckResult(bool IsTrusted, string? FailureReason)
{
	public static readonly TrustCheckResult Trusted = new(true, null);
	public static TrustCheckResult NotTrusted(string reason) => new(false, reason);
}
