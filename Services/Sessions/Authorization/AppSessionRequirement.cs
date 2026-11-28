using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Services.Sessions.Interface;
using Services.Sessions.Models;

namespace Services.Sessions.Authorization;

/// <summary>
/// The one requirement the default authorization policy adds on top of the legacy
/// "authenticated + has a NameIdentifier claim" check - see ServiceCollectionExtension.AddAuthenticationConfigured.
/// Every controller/hub using plain <c>[Authorize]</c> (no named Policy) gets this automatically,
/// which is what closes the legacy bootstrap-token bypass without editing every individual controller:
/// a bootstrap-purpose token fails <see cref="ISessionTrustChecker"/>'s first check (wrong
/// token_purpose) the same way a revoked app token fails its DB check.
/// </summary>
public sealed class AppSessionRequirement : IAuthorizationRequirement
{
	public static readonly AppSessionRequirement Instance = new();
	private AppSessionRequirement() { }
}

/// <summary>
/// ASP.NET Core's own authorization middleware reports a FAILED requirement as 403 whenever the
/// caller's JWT itself was authenticated (valid signature/issuer/audience/lifetime) - 401 is reserved
/// for the JWT failing authentication itself, before any requirement even runs (see
/// Microsoft.AspNetCore.Authorization.Policy.PolicyEvaluator: Forbid when authenticated, Challenge
/// otherwise). An ordinary business-rule 403 (e.g. DeviceSessionController's device-approval-required
/// response during enrollment) is a completely different thing and must never be confused with this
/// one - this header is only ever set from <see cref="AppSessionAuthorizationHandler"/>, i.e. only for
/// an authenticated-but-no-longer-trusted app session, never for any other 403 this API returns.
/// </summary>
public static class SessionTrustRejectionHeader
{
	public const string Name = "X-Session-Trust-Rejected";
	public const string Value = "revoked";
}

public sealed class AppSessionAuthorizationHandler(ISessionTrustChecker trustChecker, ILogger<AppSessionAuthorizationHandler> logger)
	: AuthorizationHandler<AppSessionRequirement>
{
	protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AppSessionRequirement requirement)
	{
		TrustCheckResult result = await trustChecker.CheckAsync(context.User, context.Resource is HttpContext http ? http.RequestAborted : default);
		if (result.IsTrusted)
		{
			context.Succeed(requirement);
		}
		else
		{
			// Diagnostics only - the HTTP response itself stays a uniform 401/403, never echoing
			// FailureReason to the client (plan/05-device-sessions.md's fail-closed-uniformly rule).
			logger.LogInformation("App session authorization failed: {Reason}", result.FailureReason);

			// Only marked when the caller was genuinely authenticated (a validly-signed, unexpired JWT) -
			// an anonymous/invalid-JWT caller fails authentication itself and gets an ordinary 401
			// Challenge instead, which this must never be confused with (see SessionTrustRejectionHeader's
			// own doc comment). Set directly on the response here (authorization runs before anything has
			// written to the body) rather than via a dedicated IAuthorizationMiddlewareResultHandler - the
			// existing HandleRequirementAsync shape already reaches into HttpContext for RequestAborted
			// above, so this keeps the one additional concern in the one place that already needs the
			// HttpContext, instead of adding a whole new middleware component for one header.
			if (context.Resource is HttpContext httpContext && (context.User.Identity?.IsAuthenticated ?? false))
			{
				httpContext.Response.Headers[SessionTrustRejectionHeader.Name] = SessionTrustRejectionHeader.Value;
			}
		}
	}
}
