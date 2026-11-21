using System.Security.Claims;
using Database.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Services.Sessions;
using Services.Sessions.Service;
using Xunit;

namespace SessionTests;

[Collection(DatabaseCollection.Name)]
public class SessionTrustCheckerTests(DatabaseFixture fixture)
{
	/// <summary>
	/// `legacyUserId`/`localInstanceId`/`exp` default to values that make the "happy path" claims
	/// well-formed and not-yet-expired, so every pre-existing test (which only varies
	/// accountId/sessionId/deviceId/trustVersion) keeps exercising exactly the branch it always did,
	/// not incidentally tripping over one of the three checks added for this stage's fix.
	/// </summary>
	private static ClaimsPrincipal Principal(
		string? purpose, string? accountId, string? sessionId, string? deviceId, string? trustVersion,
		string? legacyUserId = "1", string? localInstanceId = "inst-default", string? exp = null)
	{
		List<Claim> claims = [];
		if (purpose is not null) claims.Add(new Claim(SessionClaimTypes.TokenPurpose, purpose));
		if (legacyUserId is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, legacyUserId));
		if (accountId is not null) claims.Add(new Claim(SessionClaimTypes.AccountContextId, accountId));
		if (sessionId is not null) claims.Add(new Claim(SessionClaimTypes.SessionId, sessionId));
		if (deviceId is not null) claims.Add(new Claim(SessionClaimTypes.DeviceRegistrationId, deviceId));
		if (localInstanceId is not null) claims.Add(new Claim(SessionClaimTypes.LocalInstanceId, localInstanceId));
		if (trustVersion is not null) claims.Add(new Claim(SessionClaimTypes.TrustVersion, trustVersion));
		claims.Add(new Claim(JwtRegisteredClaimNames.Exp, exp ?? DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds().ToString()));
		return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
	}

	[Fact]
	public async Task CheckAsync_ForAGenuinelyTrustedSession_Succeeds()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		SessionTrustChecker checker = new(db, TimeProvider.System);
		var result = await checker.CheckAsync(Principal(
			"app", account.Id, session.Id, device.Id, device.TrustVersion.ToString(),
			legacyUserId: account.LegacyUserId.ToString(), localInstanceId: instance.Id));
		Assert.True(result.IsTrusted);
	}

	[Fact]
	public async Task CheckAsync_RejectsABootstrapPurposeToken()
	{
		await using AppDbContext db = fixture.CreateContext();
		SessionTrustChecker checker = new(db, TimeProvider.System);
		var result = await checker.CheckAsync(Principal("bootstrap", "acct", "sess", "dev", "1"));
		Assert.False(result.IsTrusted);
	}

	[Theory]
	[InlineData(null, "sess", "dev", "1")]
	[InlineData("acct", null, "dev", "1")]
	[InlineData("acct", "sess", null, "1")]
	[InlineData("acct", "sess", "dev", null)]
	[InlineData("acct", "sess", "dev", "not-a-number")]
	public async Task CheckAsync_RejectsMissingOrMalformedClaims(string? accountId, string? sessionId, string? deviceId, string? trustVersion)
	{
		await using AppDbContext db = fixture.CreateContext();
		SessionTrustChecker checker = new(db, TimeProvider.System);
		var result = await checker.CheckAsync(Principal("app", accountId, sessionId, deviceId, trustVersion));
		Assert.False(result.IsTrusted);
	}

	[Fact]
	public async Task CheckAsync_RejectsARevokedDevice_EvenWithAStillUnexpiredLookingToken()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		// The token embeds the trust version from BEFORE revocation - simulating a still
		// cryptographically-valid, unexpired JWT minted before the device was revoked.
		string tokenTrustVersion = device.TrustVersion.ToString();
		long trustVersion = device.TrustVersion;

		DeviceTrustService trustService = new();
		await trustService.RevokeDeviceAsync(db, account.Id, device.Id, trustVersion, device.Id, DateTime.UtcNow, session.Id);

		SessionTrustChecker checker = new(db, TimeProvider.System);
		var result = await checker.CheckAsync(Principal(
			"app", account.Id, session.Id, device.Id, tokenTrustVersion,
			legacyUserId: account.LegacyUserId.ToString(), localInstanceId: instance.Id));
		Assert.False(result.IsTrusted);
	}

	[Fact]
	public async Task CheckAsync_RejectsATrustVersionMismatch_WithoutASessionEnding()
	{
		// Simulates a device that was revoked and later re-approved under a new registration row
		// sharing nothing, OR a forged/stale trustVersion claim - either way, the token's claimed
		// epoch must match the device's live epoch exactly.
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		SessionTrustChecker checker = new(db, TimeProvider.System);
		var result = await checker.CheckAsync(Principal(
			"app", account.Id, session.Id, device.Id, (device.TrustVersion + 1).ToString(),
			legacyUserId: account.LegacyUserId.ToString(), localInstanceId: instance.Id));
		Assert.False(result.IsTrusted);
	}

	[Fact]
	public async Task CheckAsync_RejectsAnEndedSession()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		session.EndedAtUtc = DateTime.UtcNow;
		await db.SaveChangesAsync();

		SessionTrustChecker checker = new(db, TimeProvider.System);
		var result = await checker.CheckAsync(Principal(
			"app", account.Id, session.Id, device.Id, device.TrustVersion.ToString(),
			legacyUserId: account.LegacyUserId.ToString(), localInstanceId: instance.Id));
		Assert.False(result.IsTrusted);
	}

	[Fact]
	public async Task CheckAsync_RejectsClaimsThatDoNotMatchAnyRealSession_ForgedIdentifiers()
	{
		await using AppDbContext db = fixture.CreateContext();
		SessionTrustChecker checker = new(db, TimeProvider.System);
		var result = await checker.CheckAsync(Principal("app", TestData.NewId("acct"), TestData.NewId("sess"), TestData.NewId("dev"), "1"));
		Assert.False(result.IsTrusted);
	}

	[Fact]
	public async Task CheckAsync_RejectsAnExpiredAccessToken_EvenWithEveryOtherClaimStillCorrect()
	{
		// The one gap this stage's fix closes: a SignalR hub only authenticates/authorizes once, at
		// connect, so for a long-lived connection nothing else ever re-validates `exp` for a later
		// frame - this checker must, using its own injected TimeProvider (not DateTime.UtcNow, so this
		// is deterministic rather than racing the real clock).
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		DateTimeOffset now = DateTimeOffset.UtcNow;
		FakeTimeProvider clock = new(now);
		SessionTrustChecker checker = new(db, clock);

		string expiredExp = now.AddSeconds(-1).ToUnixTimeSeconds().ToString();
		var result = await checker.CheckAsync(Principal(
			"app", account.Id, session.Id, device.Id, device.TrustVersion.ToString(),
			legacyUserId: account.LegacyUserId.ToString(), localInstanceId: instance.Id, exp: expiredExp));
		Assert.False(result.IsTrusted);
	}

	[Fact]
	public async Task CheckAsync_AcceptsATokenThatExpiresInTheFuture_UnderTheSameFakeClock()
	{
		// The positive counterpart to the expiry test above, using the exact same FakeTimeProvider
		// instance/"now" - proves the checker compares against ITS injected clock correctly in both
		// directions, not merely "anything unparsed is rejected".
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		DateTimeOffset now = DateTimeOffset.UtcNow;
		FakeTimeProvider clock = new(now);
		SessionTrustChecker checker = new(db, clock);

		string futureExp = now.AddSeconds(1).ToUnixTimeSeconds().ToString();
		var result = await checker.CheckAsync(Principal(
			"app", account.Id, session.Id, device.Id, device.TrustVersion.ToString(),
			legacyUserId: account.LegacyUserId.ToString(), localInstanceId: instance.Id, exp: futureExp));
		Assert.True(result.IsTrusted);
	}

	[Fact]
	public async Task CheckAsync_RejectsALegacySubjectMismatch()
	{
		// The token's NameIdentifier no longer corresponds to the account's own LegacyUserId - e.g. a
		// forged/stale claim, or (defense in depth) a server-side identity remap this stage's own
		// invariants never actually produce. Must fail the same as a revoked device, not be silently
		// accepted because every session/device/trust-version field still matches.
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		SessionTrustChecker checker = new(db, TimeProvider.System);
		var result = await checker.CheckAsync(Principal(
			"app", account.Id, session.Id, device.Id, device.TrustVersion.ToString(),
			legacyUserId: (account.LegacyUserId + 1).ToString(), localInstanceId: instance.Id));
		Assert.False(result.IsTrusted);
	}

	[Fact]
	public async Task CheckAndLockDeviceForMutationAsync_ForATrustedDevice_SucceedsInsideAnAmbientTransaction()
	{
		// See Services/Hubs/BaseHub.cs's SendMessage - this is the exact calling shape: an ambient
		// transaction already open, this call locking the device row within it before the mutation.
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		await using var tx = await db.Database.BeginTransactionAsync();
		SessionTrustChecker checker = new(db, TimeProvider.System);
		var result = await checker.CheckAndLockDeviceForMutationAsync(Principal(
			"app", account.Id, session.Id, device.Id, device.TrustVersion.ToString(),
			legacyUserId: account.LegacyUserId.ToString(), localInstanceId: instance.Id));
		Assert.True(result.IsTrusted);
		await tx.CommitAsync();
	}

	[Fact]
	public async Task CheckAndLockDeviceForMutationAsync_ForARevokedDevice_FailsClosedTheSameAsCheckAsync()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");
		string staleTrustVersion = device.TrustVersion.ToString();

		DeviceTrustService trustService = new();
		await trustService.RevokeDeviceAsync(db, account.Id, device.Id, device.TrustVersion, device.Id, DateTime.UtcNow, session.Id);

		await using var tx = await db.Database.BeginTransactionAsync();
		SessionTrustChecker checker = new(db, TimeProvider.System);
		var result = await checker.CheckAndLockDeviceForMutationAsync(Principal(
			"app", account.Id, session.Id, device.Id, staleTrustVersion,
			legacyUserId: account.LegacyUserId.ToString(), localInstanceId: instance.Id));
		Assert.False(result.IsTrusted);
		await tx.RollbackAsync();
	}

	[Fact]
	public async Task CheckAsync_RejectsALocalInstanceBindingMismatch()
	{
		// The token's LocalInstanceId claim no longer matches the session's own bound LocalInstanceId -
		// e.g. a token replayed against a session it was never actually issued for.
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		SessionTrustChecker checker = new(db, TimeProvider.System);
		var result = await checker.CheckAsync(Principal(
			"app", account.Id, session.Id, device.Id, device.TrustVersion.ToString(),
			legacyUserId: account.LegacyUserId.ToString(), localInstanceId: TestData.NewId("inst-wrong")));
		Assert.False(result.IsTrusted);
	}
}
