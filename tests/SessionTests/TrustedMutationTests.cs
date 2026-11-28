using System.Security.Claims;
using Database.Context;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Services.Sessions;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Xunit;

namespace SessionTests;

/// <summary>
/// Exercises <see cref="TrustedMutation"/> directly - the one shared transaction/lock/commit-or-rollback
/// helper <c>BaseHub.SendMessage</c>/<c>ChatEntered</c>/<c>OnConnectedAsync</c> AND the HTTP mutation+
/// broadcast entrypoints (<c>ChatService.CreateChat</c>, <c>FriendshipService.AddFriend</c>/
/// <c>RespondToFriendRequest</c>) all now go through, per plan/05-device-sessions.md's "trace ALL actual
/// authenticated mutating entrypoints ... one focused reusable guard/transaction helper, no god service."
/// Testing this ONE primitive directly (rather than re-deriving the same race/commit proof once per
/// call site) is what makes every one of those call sites' own enforcement correct by construction - see
/// <see cref="SessionTrustCheckerTests"/>'s own <c>CheckAndLockDeviceForMutationAsync</c> tests for the
/// lower-level lock-acquisition proof this builds on.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TrustedMutationTests(DatabaseFixture fixture)
{
	private static ClaimsPrincipal AppPrincipal(
		Database.Entities.Sync.SyncAccountEntity account, Database.Entities.Sync.SyncDeviceRegistrationEntity device,
		Database.Entities.Sync.SyncLocalInstanceEntity instance, string sessionId)
	{
		List<Claim> claims =
		[
			new(SessionClaimTypes.TokenPurpose, SessionClaimTypes.AppPurpose),
			new(ClaimTypes.NameIdentifier, account.LegacyUserId.ToString()),
			new(SessionClaimTypes.AccountContextId, account.Id),
			new(SessionClaimTypes.SessionId, sessionId),
			new(SessionClaimTypes.DeviceRegistrationId, device.Id),
			new(SessionClaimTypes.LocalInstanceId, instance.Id),
			new(SessionClaimTypes.TrustVersion, device.TrustVersion.ToString()),
			new(JwtRegisteredClaimNames.Exp, DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds().ToString()),
		];
		return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
	}

	[Fact]
	public async Task RunAsync_ForAGenuinelyTrustedCaller_RunsTheMutationAndCommitsIt()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		SessionTrustChecker checker = new(db, TimeProvider.System);
		bool mutationRan = false;
		TrustCheckResult trust = await TrustedMutation.RunAsync(db, checker, AppPrincipal(account, device, instance, session.Id), () =>
		{
			mutationRan = true;
			return Task.CompletedTask;
		});

		Assert.True(trust.IsTrusted);
		Assert.True(mutationRan);
	}

	[Fact]
	public async Task RunAsync_ForARevokedCaller_NeverRunsTheMutationAndRollsBack()
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

		SessionTrustChecker checker = new(db, TimeProvider.System);
		bool mutationRan = false;
		ClaimsPrincipal stalePrincipal = new(new ClaimsIdentity(
		[
			new Claim(SessionClaimTypes.TokenPurpose, SessionClaimTypes.AppPurpose),
			new Claim(ClaimTypes.NameIdentifier, account.LegacyUserId.ToString()),
			new Claim(SessionClaimTypes.AccountContextId, account.Id),
			new Claim(SessionClaimTypes.SessionId, session.Id),
			new Claim(SessionClaimTypes.DeviceRegistrationId, device.Id),
			new Claim(SessionClaimTypes.LocalInstanceId, instance.Id),
			new Claim(SessionClaimTypes.TrustVersion, staleTrustVersion),
			new Claim(JwtRegisteredClaimNames.Exp, DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds().ToString()),
		], "TestAuth"));

		TrustCheckResult trust = await TrustedMutation.RunAsync(db, checker, stalePrincipal, () =>
		{
			mutationRan = true;
			return Task.CompletedTask;
		});

		Assert.False(trust.IsTrusted);
		Assert.False(mutationRan);
	}

	[Fact]
	public async Task RunAsync_WhenTheMutationItselfThrows_RollsBackRatherThanLeavingAPartialWrite()
	{
		await using AppDbContext seedDb = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(seedDb);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(seedDb, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(seedDb, device);
		var session = await TestData.CreateSessionAsync(seedDb, account, device, instance, "raw-token-1");

		await using AppDbContext db = fixture.CreateContext();
		SessionTrustChecker checker = new(db, TimeProvider.System);

		// SaveChangesAsync below actually SENDS the UPDATE to Postgres inside the still-open transaction -
		// the throw that follows must still roll it back via the transaction's own implicit dispose (never
		// reaching CommitAsync), not merely leave an in-memory, never-flushed change tracker entry behind.
		await Assert.ThrowsAsync<InvalidOperationException>(() => TrustedMutation.RunAsync(db, checker, AppPrincipal(account, device, instance, session.Id), async () =>
		{
			db.SyncDeviceRegistrations.Single(d => d.Id == device.Id).TrustVersion += 100;
			await db.SaveChangesAsync();
			throw new InvalidOperationException("simulated post-save failure");
		}));

		await using AppDbContext verifyDb = fixture.CreateContext();
		var reloaded = await verifyDb.SyncDeviceRegistrations.SingleAsync(d => d.Id == device.Id);
		Assert.Equal(device.TrustVersion, reloaded.TrustVersion); // the throwing mutation's own change never committed.
	}
}
