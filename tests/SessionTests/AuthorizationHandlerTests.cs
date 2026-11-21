using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Services.Sessions;
using Services.Sessions.Authorization;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using Xunit;

namespace SessionTests;

/// <summary>
/// Unit-level proof that the two authorization handlers (the ones the default policy and the
/// "Bootstrap" policy actually run - Services/Extensions/ServiceCollectionExtension.cs) correctly
/// succeed/fail the requirement given a fake ISessionTrustChecker, using ASP.NET Core's own public
/// AuthorizationHandlerContext constructor - this does not require a running Kestrel/TestServer. Real
/// end-to-end "a request with this JWT actually gets a 401/403" behavior is a native/integration gap
/// noted in the stage report (no dotnet run available in this environment).
/// </summary>
public class AuthorizationHandlerTests
{
	private sealed class FakeTrustChecker(bool trusted) : ISessionTrustChecker
	{
		public Task<TrustCheckResult> CheckAsync(ClaimsPrincipal principal, CancellationToken ct = default) =>
			Task.FromResult(trusted ? TrustCheckResult.Trusted : TrustCheckResult.NotTrusted("fake rejection"));

		public Task<TrustCheckResult> CheckAndLockDeviceForMutationAsync(ClaimsPrincipal principal, CancellationToken ct = default) =>
			CheckAsync(principal, ct);
	}

	private static ClaimsPrincipal AnyPrincipal(string purpose) =>
		new(new ClaimsIdentity([new Claim(SessionClaimTypes.TokenPurpose, purpose)], "TestAuth"));

	[Fact]
	public async Task AppSessionAuthorizationHandler_SucceedsWhenTrustCheckerSaysTrusted()
	{
		AppSessionAuthorizationHandler handler = new(new FakeTrustChecker(true), NullLogger<AppSessionAuthorizationHandler>.Instance);
		AuthorizationHandlerContext context = new([AppSessionRequirement.Instance], AnyPrincipal("app"), null);

		await handler.HandleAsync(context);
		Assert.True(context.HasSucceeded);
	}

	[Fact]
	public async Task AppSessionAuthorizationHandler_FailsWhenTrustCheckerSaysNotTrusted()
	{
		AppSessionAuthorizationHandler handler = new(new FakeTrustChecker(false), NullLogger<AppSessionAuthorizationHandler>.Instance);
		AuthorizationHandlerContext context = new([AppSessionRequirement.Instance], AnyPrincipal("app"), null);

		await handler.HandleAsync(context);
		Assert.False(context.HasSucceeded);
	}

	/// <summary>
	/// The client-facing "this 403 specifically means the app session is no longer trusted" marker
	/// (review4-report.md's documented gap / fix5: the client cannot tell this 403 apart from an
	/// ordinary business-rule 403 by status code alone) - only set when the caller was genuinely
	/// authenticated, never for an anonymous/invalid-JWT principal (that case fails authentication
	/// itself and the ASP.NET Core pipeline reports it as 401, never reaching Forbid at all - see
	/// SessionTrustRejectionHeader's own doc comment).
	/// </summary>
	[Fact]
	public async Task AppSessionAuthorizationHandler_SetsRejectionHeader_WhenAuthenticatedPrincipalIsNotTrusted()
	{
		AppSessionAuthorizationHandler handler = new(new FakeTrustChecker(false), NullLogger<AppSessionAuthorizationHandler>.Instance);
		DefaultHttpContext httpContext = new();
		AuthorizationHandlerContext context = new([AppSessionRequirement.Instance], AnyPrincipal("app"), httpContext);

		await handler.HandleAsync(context);

		Assert.False(context.HasSucceeded);
		Assert.Equal(SessionTrustRejectionHeader.Value, httpContext.Response.Headers[SessionTrustRejectionHeader.Name].ToString());
	}

	[Fact]
	public async Task AppSessionAuthorizationHandler_DoesNotSetRejectionHeader_WhenPrincipalIsNotAuthenticated()
	{
		AppSessionAuthorizationHandler handler = new(new FakeTrustChecker(false), NullLogger<AppSessionAuthorizationHandler>.Instance);
		DefaultHttpContext httpContext = new();
		// No authenticationType passed to ClaimsIdentity - Identity.IsAuthenticated is false, matching an
		// anonymous/invalid-JWT caller (that case never reaches Forbid at all in the real pipeline, but
		// this handler itself still runs - see AuthorizationHandlerContext's "all handlers run" semantics -
		// and must not mark a header meant exclusively for an authenticated-but-untrusted rejection).
		ClaimsPrincipal unauthenticatedPrincipal = new(new ClaimsIdentity([new Claim(SessionClaimTypes.TokenPurpose, "app")]));
		AuthorizationHandlerContext context = new([AppSessionRequirement.Instance], unauthenticatedPrincipal, httpContext);

		await handler.HandleAsync(context);

		Assert.False(context.HasSucceeded);
		Assert.True(StringValuesIsEmpty(httpContext.Response.Headers[SessionTrustRejectionHeader.Name]));
	}

	[Fact]
	public async Task AppSessionAuthorizationHandler_DoesNotSetRejectionHeader_WhenResourceIsNotHttpContext()
	{
		// AuthorizationHandlerTests' other cases (resource: null) exercise this same guard implicitly -
		// this makes the "no HttpContext available" case explicit rather than only incidental.
		AppSessionAuthorizationHandler handler = new(new FakeTrustChecker(false), NullLogger<AppSessionAuthorizationHandler>.Instance);
		AuthorizationHandlerContext context = new([AppSessionRequirement.Instance], AnyPrincipal("app"), null);

		await handler.HandleAsync(context);

		Assert.False(context.HasSucceeded);
	}

	private static bool StringValuesIsEmpty(Microsoft.Extensions.Primitives.StringValues values) => values.Count == 0;

	[Fact]
	public async Task BootstrapAuthorizationHandler_SucceedsOnlyForBootstrapPurposeClaim()
	{
		BootstrapAuthorizationHandler handler = new();

		AuthorizationHandlerContext bootstrapContext = new([BootstrapRequirement.Instance], AnyPrincipal("bootstrap"), null);
		await handler.HandleAsync(bootstrapContext);
		Assert.True(bootstrapContext.HasSucceeded);

		AuthorizationHandlerContext appContext = new([BootstrapRequirement.Instance], AnyPrincipal("app"), null);
		await handler.HandleAsync(appContext);
		Assert.False(appContext.HasSucceeded);

		AuthorizationHandlerContext noClaimContext = new([BootstrapRequirement.Instance], new ClaimsPrincipal(new ClaimsIdentity()), null);
		await handler.HandleAsync(noClaimContext);
		Assert.False(noClaimContext.HasSucceeded);
	}
}
