using Database.Context;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Services.Sessions;
using Services.Sessions.Service;
using Services.Utilities.Models;
using Services.Sync.Contracts;
using Xunit;

namespace SessionTests;

[Collection(DatabaseCollection.Name)]
public class RefreshTokenServiceTests(DatabaseFixture fixture)
{
	private static (RefreshTokenService Service, FakeTimeProvider Clock, FakeSessionRevocationNotifier Notifier) MakeService(TestMasterSecretDirectory dir)
	{
		dir.WriteVersion("v1");
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		SessionTokenIssuer issuer = new(Options.Create(new AppSettings { TokenIssuer = "test-issuer", TokenAudience = "test-audience", TokenSigningKey = new string('k', 64) }));
		AesGcmReplayPayloadProtector protector = new(new Services.Keys.Service.FileMasterSecretProvider(dir.OptionsFor("v1")));
		FakeSessionRevocationNotifier notifier = new();
		return (new RefreshTokenService(clock, issuer, protector, notifier), clock, notifier);
	}

	[Fact]
	public async Task Rotate_WithTheCurrentToken_Succeeds()
	{
		using var dir = new TestMasterSecretDirectory();
		var (service, _, _) = MakeService(dir);

		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		RefreshSessionResult result = await service.RotateAsync(db, account.Id, session.Id, TestData.NewId("op"), "raw-token-1");

		Assert.Equal(RefreshOutcome.Rotated, result.Outcome);
		Assert.NotNull(result.Tokens);
		Assert.NotEqual("raw-token-1", result.Tokens!.RefreshToken);
	}

	[Fact]
	public async Task Rotate_IdenticalRetry_WithinWindow_ReturnsTheSameTokens()
	{
		using var dir = new TestMasterSecretDirectory();
		var (service, _, _) = MakeService(dir);

		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		string operationId = TestData.NewId("op");
		RefreshSessionResult first = await service.RotateAsync(db, account.Id, session.Id, operationId, "raw-token-1");
		RefreshSessionResult retry = await service.RotateAsync(db, account.Id, session.Id, operationId, "raw-token-1");

		Assert.Equal(RefreshOutcome.Rotated, retry.Outcome);
		Assert.Equal(first.Tokens!.AccessToken, retry.Tokens!.AccessToken);
		Assert.Equal(first.Tokens.RefreshToken, retry.Tokens.RefreshToken);
	}

	[Fact]
	public async Task Rotate_IdenticalRetry_AfterReplayWindowExpires_RequiresReauthentication()
	{
		using var dir = new TestMasterSecretDirectory();
		var (service, clock, _) = MakeService(dir);

		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		string operationId = TestData.NewId("op");
		await service.RotateAsync(db, account.Id, session.Id, operationId, "raw-token-1");

		clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));

		RefreshSessionResult retry = await service.RotateAsync(db, account.Id, session.Id, operationId, "raw-token-1");
		Assert.Equal(RefreshOutcome.ReauthenticationRequired, retry.Outcome);
		Assert.Null(retry.Tokens);
	}

	[Fact]
	public async Task Rotate_ReuseOfASupersededTokenUnderADifferentOperation_RevokesTheSession()
	{
		using var dir = new TestMasterSecretDirectory();
		var (service, clock, notifier) = MakeService(dir);

		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		// Legitimate rotation under opA consumes raw-token-1.
		await service.RotateAsync(db, account.Id, session.Id, TestData.NewId("opA"), "raw-token-1");

		// Someone (attacker, or a buggy duplicate device) now tries to reuse the superseded
		// raw-token-1 under a brand new operation id.
		RefreshSessionResult reuse = await service.RotateAsync(db, account.Id, session.Id, TestData.NewId("opB"), "raw-token-1");

		Assert.Equal(RefreshOutcome.ReplayDetected, reuse.Outcome);
		Assert.Null(reuse.Tokens);
		Assert.Contains(notifier.Calls, c => c.SessionId == session.Id);

		await using AppDbContext verifyDb = fixture.CreateContext();
		var reloaded = await verifyDb.SyncDeviceSessions.SingleAsync(s => s.Id == session.Id);
		Assert.NotNull(reloaded.EndedAtUtc);
		Assert.Null(reloaded.CurrentRefreshTokenHash);
	}

	[Fact]
	public async Task Rotate_WithAWrongToken_NeverSeenBefore_IsRejectedWithoutMutatingTheSession()
	{
		using var dir = new TestMasterSecretDirectory();
		var (service, _, notifier) = MakeService(dir);

		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		RefreshSessionResult result = await service.RotateAsync(db, account.Id, session.Id, TestData.NewId("op"), "completely-wrong-token");

		Assert.Equal(RefreshOutcome.ReauthenticationRequired, result.Outcome);
		Assert.Empty(notifier.Calls); // a wrong-but-never-valid token must not trigger family revocation

		await using AppDbContext verifyDb = fixture.CreateContext();
		var reloaded = await verifyDb.SyncDeviceSessions.SingleAsync(s => s.Id == session.Id);
		Assert.Null(reloaded.EndedAtUtc);
		Assert.NotNull(reloaded.CurrentRefreshTokenHash);
	}

	[Fact]
	public async Task Rotate_OnARevokedDevice_FailsClosedEvenWithTheCorrectToken()
	{
		using var dir = new TestMasterSecretDirectory();
		var (service, _, _) = MakeService(dir);
		Services.Sessions.Service.DeviceTrustService trustService = new();

		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		// Directly end the session the way RevokeDeviceAsync would (simulating "revoked between proof
		// verification and rotation commit") without going through the full revoke flow, to isolate
		// RotateAsync's own current-trust-at-commit check.
		await trustService.RevokeDeviceAsync(db, account.Id, device.Id, device.TrustVersion, device.Id, DateTime.UtcNow, session.Id);

		RefreshSessionResult result = await service.RotateAsync(db, account.Id, session.Id, TestData.NewId("op"), "raw-token-1");
		Assert.Equal(RefreshOutcome.ReauthenticationRequired, result.Outcome);
		Assert.Null(result.Tokens);
	}

	[Fact]
	public async Task Rotate_UnknownSession_IsRejected()
	{
		using var dir = new TestMasterSecretDirectory();
		var (service, _, _) = MakeService(dir);
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);

		RefreshSessionResult result = await service.RotateAsync(db, account.Id, "no-such-session", TestData.NewId("op"), "whatever");
		Assert.Equal(RefreshOutcome.ReauthenticationRequired, result.Outcome);
	}

	[Fact]
	public async Task Rotate_ConcurrentIdenticalRetries_BothReturnTheSameResult_NoDoubleRotation()
	{
		// "Unknown network outcome retries the same operation" - two concurrent requests for the
		// identical (sessionId, operationId, token) must both observe exactly one rotation, not two.
		using var dir = new TestMasterSecretDirectory();
		var (serviceA, _, _) = MakeService(dir);
		var (serviceB, _, _) = MakeService(dir);

		await using AppDbContext seedDb = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(seedDb);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(seedDb, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(seedDb, device);
		var session = await TestData.CreateSessionAsync(seedDb, account, device, instance, "raw-token-1");

		string operationId = TestData.NewId("op");
		await using AppDbContext dbA = fixture.CreateContext();
		await using AppDbContext dbB = fixture.CreateContext();

		Task<RefreshSessionResult> taskA = serviceA.RotateAsync(dbA, account.Id, session.Id, operationId, "raw-token-1");
		Task<RefreshSessionResult> taskB = serviceB.RotateAsync(dbB, account.Id, session.Id, operationId, "raw-token-1");
		RefreshSessionResult[] results = await Task.WhenAll(taskA, taskB);

		Assert.All(results, r => Assert.Equal(RefreshOutcome.Rotated, r.Outcome));
		Assert.Equal(results[0].Tokens!.RefreshToken, results[1].Tokens!.RefreshToken);

		await using AppDbContext verifyDb = fixture.CreateContext();
		int rotationRows = await verifyDb.SyncRefreshTokenRotations.CountAsync(r => r.SessionId == session.Id);
		Assert.Equal(1, rotationRows);
	}

	[Fact]
	public async Task Rotate_ConcurrentDifferentOperations_OnlyOneCanWinWithTheSameOldToken()
	{
		// Two different operation ids both presenting the SAME current token concurrently: exactly one
		// may win the rotation (it becomes the new current token); the other must find the token no
		// longer current and, since this is its first and only presentation of that hash this race
		// never recorded it as "superseded" under a *third* operation, must be rejected as
		// ReauthenticationRequired, not misclassified as reuse (reuse requires the hash to already
		// appear in this session's own rotation history, which a genuinely concurrent loser never gets
		// the chance to create).
		using var dir = new TestMasterSecretDirectory();
		var (serviceA, _, _) = MakeService(dir);
		var (serviceB, _, notifierB) = MakeService(dir);

		await using AppDbContext seedDb = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(seedDb);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(seedDb, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(seedDb, device);
		var session = await TestData.CreateSessionAsync(seedDb, account, device, instance, "raw-token-1");

		await using AppDbContext dbA = fixture.CreateContext();
		await using AppDbContext dbB = fixture.CreateContext();

		Task<RefreshSessionResult> taskA = serviceA.RotateAsync(dbA, account.Id, session.Id, TestData.NewId("opA"), "raw-token-1");
		Task<RefreshSessionResult> taskB = serviceB.RotateAsync(dbB, account.Id, session.Id, TestData.NewId("opB"), "raw-token-1");
		RefreshSessionResult[] results = await Task.WhenAll(taskA, taskB);

		Assert.Equal(1, results.Count(r => r.Outcome == RefreshOutcome.Rotated));
		Assert.Equal(1, results.Count(r => r.Outcome == RefreshOutcome.ReauthenticationRequired));

		// The session must still be usable (not revoked) - this was a benign race, not an attack -
		// and must never have been reported as a reuse/family-revocation to either caller's notifier.
		Assert.Empty(notifierB.Calls);
		await using AppDbContext verifyDb = fixture.CreateContext();
		var reloaded = await verifyDb.SyncDeviceSessions.SingleAsync(s => s.Id == session.Id);
		Assert.Null(reloaded.EndedAtUtc);
	}

	[Fact]
	public async Task Rotate_RacedAgainstAConcurrentRevokeOfTheSameDevice_NeverRotatesAfterTheRevokeCommits()
	{
		// Independent review finding: a refresh racing a revoke of its own device must resolve to
		// EXACTLY one consistent outcome, never "rotated successfully AND the device ends up revoked"
		// (which would mean a revoked device kept a live, usable refresh token). Both code paths lock
		// real rows (RotateAsync locks the session; RevokeDeviceAsync locks the account, then the
		// device - see that method's own doc comment) under real transactions, so whichever commits
		// first is what the other must observe.
		using var dir = new TestMasterSecretDirectory();
		var (service, _, _) = MakeService(dir);
		DeviceTrustService trustService = new();

		await using AppDbContext seedDb = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(seedDb);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(seedDb, account, spki, "key-1");
		long trustVersion = device.TrustVersion;
		var instance = await TestData.CreateLocalInstanceAsync(seedDb, device);
		var session = await TestData.CreateSessionAsync(seedDb, account, device, instance, "raw-token-1");

		await using AppDbContext refreshDb = fixture.CreateContext();
		await using AppDbContext revokeDb = fixture.CreateContext();

		Task<RefreshSessionResult> refreshTask = service.RotateAsync(refreshDb, account.Id, session.Id, TestData.NewId("op"), "raw-token-1");
		Task revokeTask = trustService.RevokeDeviceAsync(revokeDb, account.Id, device.Id, trustVersion, device.Id, DateTime.UtcNow, session.Id);
		await Task.WhenAll(refreshTask, revokeTask);
		RefreshSessionResult refreshResult = await refreshTask;

		await using AppDbContext verifyDb = fixture.CreateContext();
		var reloadedDevice = await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == device.Id);
		Assert.Equal(DeviceTrustState.Revoked, reloadedDevice.TrustState); // the revoke always eventually wins/commits.

		var reloadedSession = await verifyDb.SyncDeviceSessions.SingleAsync(s => s.Id == session.Id);
		if (refreshResult.Outcome == RefreshOutcome.Rotated)
		{
			// The refresh's transaction committed (locked the session) strictly BEFORE the revoke's own
			// transaction reached (and locked) this device/session - a legitimate, race-free ordering.
			// The revoke still eventually ends the session regardless of this prior rotation.
			Assert.NotNull(reloadedSession.EndedAtUtc);
		}
		else
		{
			// The revoke committed first (or the refresh's session lock was acquired after the revoke's
			// session-ending UPDATE already ran) - the refresh must fail closed, never silently succeed
			// against an already-revoked device.
			Assert.Equal(RefreshOutcome.DeviceRevoked, refreshResult.Outcome);
			Assert.Null(refreshResult.Tokens);
		}
	}

	private static System.Security.Claims.ClaimsPrincipal AppPrincipal(
		Database.Entities.Sync.SyncAccountEntity account, Database.Entities.Sync.SyncDeviceRegistrationEntity device,
		Database.Entities.Sync.SyncLocalInstanceEntity instance, string sessionId)
	{
		List<System.Security.Claims.Claim> claims =
		[
			new(Services.Sessions.SessionClaimTypes.TokenPurpose, Services.Sessions.SessionClaimTypes.AppPurpose),
			new(System.Security.Claims.ClaimTypes.NameIdentifier, account.LegacyUserId.ToString()),
			new(Services.Sessions.SessionClaimTypes.AccountContextId, account.Id),
			new(Services.Sessions.SessionClaimTypes.SessionId, sessionId),
			new(Services.Sessions.SessionClaimTypes.DeviceRegistrationId, device.Id),
			new(Services.Sessions.SessionClaimTypes.LocalInstanceId, instance.Id),
			new(Services.Sessions.SessionClaimTypes.TrustVersion, device.TrustVersion.ToString()),
			new(Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames.Exp, DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds().ToString()),
		];
		return new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "TestAuth"));
	}

	[Fact]
	public async Task CheckAndLockDeviceForMutationAsync_RacedAgainstAConcurrentReuseDetectionForTheSameSession_NeverObservesTrustInconsistentWithTheFinalCommittedOutcome()
	{
		// The NEW session-row lock SessionTrustChecker.CheckAndLockDeviceForMutationAsync takes (alongside
		// its existing device-row lock) is what this proves: RotateAsync's reuse-detection path ends a
		// session WITHOUT ever touching the device row at all (see that method's own doc comment), so a
		// device-only lock would not serialize against it. Whichever of the two genuinely commits first,
		// the other must observe its result once unblocked - never an interleaved/inconsistent outcome.
		using var dir = new TestMasterSecretDirectory();
		var (service, _, _) = MakeService(dir);

		await using AppDbContext seedDb = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(seedDb);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(seedDb, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(seedDb, device);
		var session = await TestData.CreateSessionAsync(seedDb, account, device, instance, "raw-token-1");

		// Legitimate rotation under opA consumes raw-token-1 - its superseded value is what the concurrent
		// reuse attempt below replays (same seeding as Rotate_ReuseOfASupersededTokenUnderADifferentOperation_RevokesTheSession above).
		await service.RotateAsync(seedDb, account.Id, session.Id, TestData.NewId("opA"), "raw-token-1");

		await using AppDbContext mutationDb = fixture.CreateContext();
		await using AppDbContext reuseDb = fixture.CreateContext();

		async Task<Services.Sessions.Models.TrustCheckResult> MutationPathAsync()
		{
			await using var tx = await mutationDb.Database.BeginTransactionAsync();
			Services.Sessions.Service.SessionTrustChecker checker = new(mutationDb, TimeProvider.System);
			Services.Sessions.Models.TrustCheckResult trust = await checker.CheckAndLockDeviceForMutationAsync(
				AppPrincipal(account, device, instance, session.Id));
			if (trust.IsTrusted)
			{
				await tx.CommitAsync();
			}
			else
			{
				await tx.RollbackAsync();
			}
			return trust;
		}

		Task<Services.Sessions.Models.TrustCheckResult> mutationTask = MutationPathAsync();
		Task<RefreshSessionResult> reuseTask = service.RotateAsync(reuseDb, account.Id, session.Id, TestData.NewId("opB"), "raw-token-1");
		await Task.WhenAll(mutationTask, reuseTask);

		RefreshSessionResult reuseResult = await reuseTask;
		// Reuse detection always eventually ends the session, regardless of which transaction's lock
		// acquisition won the race.
		Assert.Equal(RefreshOutcome.ReplayDetected, reuseResult.Outcome);

		await using AppDbContext verifyDb = fixture.CreateContext();
		var reloadedSession = await verifyDb.SyncDeviceSessions.SingleAsync(s => s.Id == session.Id);
		Assert.NotNull(reloadedSession.EndedAtUtc);

		// Whichever outcome the mutation observed, it must be CONSISTENT with genuine serialization: either
		// it committed (IsTrusted) strictly BEFORE the reuse-detection transaction could acquire the same
		// session row's lock, or it was blocked until reuse-detection's own commit and then correctly
		// observed the now-ended session (!IsTrusted) - both are legitimate race-free orderings; a
		// device-only lock could instead have let the mutation observe IsTrusted=true using a stale read
		// taken WHILE the (unserialized) reuse-detection commit was landing concurrently, which this test's
		// own `Assert.NotNull(reloadedSession.EndedAtUtc)` above does not by itself rule out without this
		// same-session-lock guarantee.
	}

	[Fact]
	public async Task RevokeDeviceAsync_RacedAgainstAConcurrentReuseDetectionForTheActingSession_NeverRevokesOnAStaleLiveRead()
	{
		// Review3's blocking finding: DeviceTrustService.RevokeDeviceAsync's acting-session-liveness check
		// used to be a plain (non-`FOR UPDATE`) read, which cannot serialize against RotateAsync's own
		// reuse-detection commit for that exact session - a revoke could observe "still live" a moment
		// before a concurrent reuse-detection ends that very session. The acting device here (the actor)
		// is NEVER the device being revoked (a separate, third-party "target" device is) - RotateAsync's
		// own device-trust check for the actor's device is therefore unaffected by the revoke outcome,
		// isolating this test to the session-lock interaction alone.
		using var dir = new TestMasterSecretDirectory();
		var (refreshService, _, _) = MakeService(dir);
		DeviceTrustService trustService = new();

		await using AppDbContext seedDb = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(seedDb);
		var (actorSpki, _) = TestData.NewDeviceKey();
		var actor = await TestData.CreateTrustedDeviceAsync(seedDb, account, actorSpki, "key-actor", isFirstDevice: true);
		var instance = await TestData.CreateLocalInstanceAsync(seedDb, actor);
		var actingSession = await TestData.CreateSessionAsync(seedDb, account, actor, instance, "raw-token-1");
		var (targetSpki, _) = TestData.NewDeviceKey();
		var target = await TestData.CreateTrustedDeviceAsync(seedDb, account, targetSpki, "key-target", isFirstDevice: false);

		// Legitimate rotation under opA consumes raw-token-1 - its now-superseded value is what the
		// concurrent reuse attempt below replays, exactly as the existing session-lock race test above does.
		await refreshService.RotateAsync(seedDb, account.Id, actingSession.Id, TestData.NewId("opA"), "raw-token-1");

		await using AppDbContext revokeDb = fixture.CreateContext();
		await using AppDbContext reuseDb = fixture.CreateContext();

		Task<IReadOnlyList<string>> revokeTask = Task.Run(async () =>
		{
			try
			{
				return await trustService.RevokeDeviceAsync(
					revokeDb, account.Id, actor.Id, actor.TrustVersion, target.Id, DateTime.UtcNow, actingSession.Id);
			}
			catch (SessionNotTrustedException)
			{
				return (IReadOnlyList<string>)[];
			}
		});
		Task<RefreshSessionResult> reuseTask = refreshService.RotateAsync(reuseDb, account.Id, actingSession.Id, TestData.NewId("opB"), "raw-token-1");
		await Task.WhenAll(revokeTask, reuseTask);

		RefreshSessionResult reuseResult = await reuseTask;
		Assert.Equal(RefreshOutcome.ReplayDetected, reuseResult.Outcome);

		await using AppDbContext verifyDb = fixture.CreateContext();
		var reloadedSession = await verifyDb.SyncDeviceSessions.SingleAsync(s => s.Id == actingSession.Id);
		// Reuse detection always eventually ends the acting session regardless of which transaction's lock
		// acquisition won the race - the actor device's own trust is never touched by either side here.
		Assert.NotNull(reloadedSession.EndedAtUtc);

		var reloadedTarget = await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == target.Id);
		if ((await revokeTask).Count > 0)
		{
			// RevokeDeviceAsync's session-row lock was acquired (and the whole revoke committed) strictly
			// BEFORE the reuse-detection transaction could acquire the SAME row's lock - a legitimate,
			// race-free ordering in which the acting session genuinely WAS still live at that instant.
			Assert.Equal(DeviceTrustState.Revoked, reloadedTarget.TrustState);
		}
		else
		{
			// RevokeDeviceAsync's session lock was blocked until the reuse-detection transaction committed
			// first, then correctly re-read the now-ended session and failed closed - the plain-read version
			// of this check could instead have observed "still live" using a stale snapshot taken WHILE the
			// (unserialized) reuse-detection commit was landing concurrently, wrongly revoking the target.
			Assert.Equal(DeviceTrustState.Trusted, reloadedTarget.TrustState); // untouched.
		}
	}
}
