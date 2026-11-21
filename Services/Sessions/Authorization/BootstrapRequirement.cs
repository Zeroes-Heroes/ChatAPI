using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Services.Sessions;

namespace Services.Sessions.Authorization;

/// <summary>Named "Bootstrap" policy for exactly the two device-enrollment endpoints: requires only the legacy credential-verified token_purpose claim, never a currently-trusted session (there is no session yet at this point in the flow). Deliberately does not grant access to anything else - the default policy (<see cref="AppSessionRequirement"/>) rejects a bootstrap-purpose token outright.</summary>
public sealed class BootstrapRequirement : IAuthorizationRequirement
{
	public static readonly BootstrapRequirement Instance = new();
	private BootstrapRequirement() { }
}

public sealed class BootstrapAuthorizationHandler : AuthorizationHandler<BootstrapRequirement>
{
	protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, BootstrapRequirement requirement)
	{
		if (context.User.FindFirstValue(SessionClaimTypes.TokenPurpose) == SessionClaimTypes.BootstrapPurpose)
		{
			context.Succeed(requirement);
		}
		return Task.CompletedTask;
	}
}
