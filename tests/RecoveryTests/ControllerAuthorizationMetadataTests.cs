using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Services.Extensions;
using Services.Sessions;
using Turbo.Controllers.DeviceSession.Controller;
using Turbo.Controllers.Recovery.Controller;
using Xunit;

namespace RecoveryTests;

/// <summary>
/// Composes the REAL controller/action authorization attributes with the REAL configured policies.
/// Testing the handlers alone cannot catch this: ASP.NET Core ANDs controller- and action-level
/// policies, while this project's app-session and bootstrap requirements demand mutually exclusive
/// token purposes - so a class-level [Authorize] would make every bootstrap endpoint unreachable.
/// </summary>
public class ControllerAuthorizationMetadataTests
{
	private static IAuthorizationPolicyProvider PolicyProvider()
	{
		IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["TokenIssuer"] = "test-issuer",
			["TokenAudience"] = "test-audience",
			["TokenSigningKey"] = new string('k', 64),
		}).Build();

		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton(configuration);
		// The REAL policy configuration this app runs with - not a hand-written copy of it.
		services.AddAuthenticationConfigured(configuration);
		return services.BuildServiceProvider().GetRequiredService<IAuthorizationPolicyProvider>();
	}

	/// <summary>Every authorization attribute that actually applies to one action: the action's own plus its controller's.</summary>
	private static IReadOnlyList<IAuthorizeData> AuthorizeDataFor(Type controller, string actionName)
	{
		MethodInfo action = controller.GetMethod(actionName)
			?? throw new InvalidOperationException($"{controller.Name}.{actionName} not found.");
		return
		[
			.. controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true),
			.. action.GetCustomAttributes<AuthorizeAttribute>(inherit: true),
		];
	}

	private static async Task<AuthorizationPolicy> EffectivePolicyAsync(Type controller, string actionName) =>
		await AuthorizationPolicy.CombineAsync(PolicyProvider(), AuthorizeDataFor(controller, actionName))
		?? throw new InvalidOperationException("No policy applies to this action.");

	private static ClaimsPrincipal TokenWithPurpose(string purpose) =>
		new(new ClaimsIdentity(
		[
			new Claim(ClaimTypes.NameIdentifier, "42"),
			new Claim(SessionClaimTypes.TokenPurpose, purpose),
		], "test"));

	public static TheoryData<Type, string> BootstrapActions() => new()
	{
		{ typeof(DeviceSessionController), nameof(DeviceSessionController.RequestChallenge) },
		{ typeof(DeviceSessionController), nameof(DeviceSessionController.Register) },
		{ typeof(RecoveryController), nameof(RecoveryController.RequestRecoveryChallenge) },
		{ typeof(RecoveryController), nameof(RecoveryController.RegisterWithRecovery) },
		{ typeof(RecoveryController), nameof(RecoveryController.StartNewContext) },
	};

	public static TheoryData<Type, string> AppSessionActions() => new()
	{
		{ typeof(DeviceSessionController), nameof(DeviceSessionController.Revoke) },
		{ typeof(RecoveryController), nameof(RecoveryController.GetEnvelope) },
		{ typeof(RecoveryController), nameof(RecoveryController.StoreEnvelope) },
		{ typeof(RecoveryController), nameof(RecoveryController.ConfirmEnvelope) },
		{ typeof(RecoveryController), nameof(RecoveryController.GetApprovalRequests) },
		{ typeof(RecoveryController), nameof(RecoveryController.Approve) },
		{ typeof(RecoveryController), nameof(RecoveryController.GetAuthorKeyEscrows) },
		{ typeof(RecoveryController), nameof(RecoveryController.StoreAuthorKeyEscrows) },
		{ typeof(RecoveryController), nameof(RecoveryController.GetHandoff) },
		{ typeof(RecoveryController), nameof(RecoveryController.AcknowledgeHandoff) },
	};

	[Theory]
	[MemberData(nameof(BootstrapActions))]
	public async Task BootstrapEndpoints_RequireOnlyTheBootstrapRequirement(Type controller, string actionName)
	{
		AuthorizationPolicy policy = await EffectivePolicyAsync(controller, actionName);

		// The decisive assertion: the app-session requirement must NOT also apply, or a bootstrap token
		// (the only credential these endpoints can have) could never satisfy the combined policy.
		Assert.DoesNotContain(policy.Requirements, r => r is Services.Sessions.Authorization.AppSessionRequirement);
		Assert.Contains(policy.Requirements, r => r is Services.Sessions.Authorization.BootstrapRequirement);
	}

	[Theory]
	[MemberData(nameof(AppSessionActions))]
	public async Task AppSessionEndpoints_RequireTheCurrentlyTrustedSessionRequirement(Type controller, string actionName)
	{
		AuthorizationPolicy policy = await EffectivePolicyAsync(controller, actionName);

		Assert.Contains(policy.Requirements, r => r is Services.Sessions.Authorization.AppSessionRequirement);
		Assert.DoesNotContain(policy.Requirements, r => r is Services.Sessions.Authorization.BootstrapRequirement);
	}

	[Theory]
	[MemberData(nameof(BootstrapActions))]
	public async Task ABootstrapTokenSatisfiesTheBootstrapRequirement(Type controller, string actionName)
	{
		AuthorizationPolicy policy = await EffectivePolicyAsync(controller, actionName);
		Services.Sessions.Authorization.BootstrapAuthorizationHandler handler = new();

		AuthorizationHandlerContext context = new(policy.Requirements, TokenWithPurpose(SessionClaimTypes.BootstrapPurpose), resource: null);
		await handler.HandleAsync(context);
		await new Microsoft.AspNetCore.Authorization.Infrastructure.PassThroughAuthorizationHandler().HandleAsync(context);

		// Evaluate the built-in authenticated-user and name-identifier requirements too, as ASP.NET
		// Core does. A class-level app-session policy would still leave its requirement unsatisfied.
		Assert.True(context.HasSucceeded);
	}

	[Fact]
	public void NeitherRecoveryNorDeviceSessionController_UsesAllowAnonymousToReachBootstrapEndpoints()
	{
		foreach ((Type controller, string actionName) in BootstrapActions().Select(d => ((Type)d[0], (string)d[1])))
		{
			MethodInfo action = controller.GetMethod(actionName)!;
			Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
			Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
		}
	}
}
