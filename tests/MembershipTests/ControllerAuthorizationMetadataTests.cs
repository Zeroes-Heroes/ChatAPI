using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Services.Extensions;
using Services.Sessions.Authorization;
using Turbo.Controllers.Chat.Controller;
using Xunit;

namespace MembershipTests;

/// <summary>
/// The stage-07 membership and stage-08 history endpoints must be reachable ONLY with a currently-trusted
/// app session - the
/// same real policy composition check stage 06 introduced for its own controllers (ASP.NET Core ANDs
/// controller- and action-level policies, and this project's app-session and bootstrap requirements
/// demand mutually exclusive token purposes, so neither a bootstrap token nor an anonymous caller may
/// ever reach a composition change).
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

	private static async Task<AuthorizationPolicy> EffectivePolicyAsync(string actionName)
	{
		MethodInfo action = typeof(ChatController).GetMethod(actionName)
			?? throw new InvalidOperationException($"ChatController.{actionName} not found.");
		IReadOnlyList<IAuthorizeData> data =
		[
			.. typeof(ChatController).GetCustomAttributes<AuthorizeAttribute>(inherit: true),
			.. action.GetCustomAttributes<AuthorizeAttribute>(inherit: true),
		];

		return await AuthorizationPolicy.CombineAsync(PolicyProvider(), data)
			?? throw new InvalidOperationException("No policy applies to this action.");
	}

	public static TheoryData<string> ChatActions() =>
	[
		nameof(ChatController.CreateChat),
		nameof(ChatController.GetChats),
		nameof(ChatController.AddMembers),
		nameof(ChatController.RemoveMember),
		nameof(ChatController.Leave),
		// Stage 08: reading permitted history and confirming an archive download are just as much
		// account-scoped operations as a composition change.
		nameof(ChatController.GetHistory),
		nameof(ChatController.ConfirmHistoryStored),
	];

	[Theory]
	[MemberData(nameof(ChatActions))]
	public async Task ChatEndpoints_RequireTheCurrentlyTrustedAppSessionRequirement(string actionName)
	{
		AuthorizationPolicy policy = await EffectivePolicyAsync(actionName);

		Assert.Contains(policy.Requirements, requirement => requirement is AppSessionRequirement);
		Assert.DoesNotContain(policy.Requirements, requirement => requirement is BootstrapRequirement);
	}

	[Theory]
	[MemberData(nameof(ChatActions))]
	public void ChatEndpoints_AreNeverAnonymous(string actionName)
	{
		MethodInfo action = typeof(ChatController).GetMethod(actionName)!;

		Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
		Assert.Empty(typeof(ChatController).GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
	}
}
