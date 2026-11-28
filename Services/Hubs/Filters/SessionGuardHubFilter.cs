using Microsoft.AspNetCore.SignalR;
using Services.Sessions.Interface;
using Services.Sessions.Models;

namespace Services.Hubs.Filters;

/// <summary>
/// Closes the gap plain <c>[Authorize]</c> on a Hub cannot: ASP.NET Core authorization for a Hub only
/// runs once, at connection handshake time, never again for the lifetime of that (potentially
/// long-lived) connection. This filter re-runs the exact same <see cref="ISessionTrustChecker"/> check
/// the default HTTP authorization policy uses (see <see cref="Services.Sessions.Authorization.AppSessionRequirement"/>)
/// on every single hub invocation, including <see cref="OnConnectedAsync"/> itself, per
/// plan/05-device-sessions.md: "checked EVERY HTTP+SignalR operation, not merely on connect/token
/// issuance." A failed check aborts the connection outright - a revoked/trust-changed connection must
/// never be left open merely because it stopped calling anything.
/// </summary>
public sealed class SessionGuardHubFilter(ISessionTrustChecker trustChecker) : IHubFilter
{
	public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
	{
		TrustCheckResult result = await trustChecker.CheckAsync(invocationContext.Context.User, invocationContext.Context.ConnectionAborted);
		if (!result.IsTrusted)
		{
			invocationContext.Context.Abort();
			throw new HubException("Session is not currently trusted.");
		}

		return await next(invocationContext);
	}

	public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
	{
		TrustCheckResult result = await trustChecker.CheckAsync(context.Context.User, context.Context.ConnectionAborted);
		if (!result.IsTrusted)
		{
			context.Context.Abort();
			return;
		}

		await next(context);
	}
}
