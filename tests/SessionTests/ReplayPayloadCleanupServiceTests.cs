using Database.Context;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Sessions.Service;
using Xunit;

namespace SessionTests;

/// <summary>
/// Independent review finding: nothing previously purged <see cref="SyncRefreshTokenRotationEntity.ProtectedReplayPayload"/>
/// once its replay window passed, despite the entity's own doc comment saying it would be. These tests
/// exercise <see cref="ReplayPayloadCleanupService"/> directly against real rows/real SQL - the
/// `ctid`-bounded batch UPDATE itself only needs to be proven correct, not run through the full
/// RefreshTokenService rotation flow.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ReplayPayloadCleanupServiceTests(DatabaseFixture fixture)
{
	private static SyncRefreshTokenRotationEntity NewRotation(string sessionId, string operationId, DateTime replayExpiresAtUtc) =>
		new(sessionId, operationId, [1, 2, 3], [4, 5, 6], DateTime.UtcNow, replayExpiresAtUtc, [9, 9, 9])
		{
			ProtectedReplayNonce = [1],
			ProtectedReplayTag = [2],
			ProtectedReplayMasterKeyVersion = "v1",
		};

	[Fact]
	public async Task PurgeExpiredAsync_NullsThePayloadFieldsOnlyForRowsPastTheirReplayWindow_KeepingTheHashes()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		DateTime now = DateTime.UtcNow;
		SyncRefreshTokenRotationEntity expired = NewRotation(session.Id, TestData.NewId("op-expired"), now.AddMinutes(-1));
		SyncRefreshTokenRotationEntity stillLive = NewRotation(session.Id, TestData.NewId("op-live"), now.AddMinutes(10));
		db.SyncRefreshTokenRotations.AddRange(expired, stillLive);
		await db.SaveChangesAsync();

		ReplayPayloadCleanupService cleanup = new();
		int purged = await cleanup.PurgeExpiredAsync(db, now, batchSize: 500);
		Assert.Equal(1, purged);

		await using AppDbContext verifyDb = fixture.CreateContext();
		var reloadedExpired = await verifyDb.SyncRefreshTokenRotations.AsNoTracking()
			.SingleAsync(r => r.SessionId == session.Id && r.RefreshOperationId == expired.RefreshOperationId);
		Assert.Null(reloadedExpired.ProtectedReplayPayload);
		Assert.Null(reloadedExpired.ProtectedReplayNonce);
		Assert.Null(reloadedExpired.ProtectedReplayTag);
		Assert.Null(reloadedExpired.ProtectedReplayMasterKeyVersion);
		// Reuse detection needs these for the lifetime of the session, never purged by this cleanup.
		Assert.Equal(new byte[] { 1, 2, 3 }, reloadedExpired.OldRefreshTokenHash);
		Assert.Equal(new byte[] { 4, 5, 6 }, reloadedExpired.NewRefreshTokenHash);

		var reloadedLive = await verifyDb.SyncRefreshTokenRotations.AsNoTracking()
			.SingleAsync(r => r.SessionId == session.Id && r.RefreshOperationId == stillLive.RefreshOperationId);
		Assert.NotNull(reloadedLive.ProtectedReplayPayload);
	}

	[Fact]
	public async Task PurgeExpiredAsync_IsBoundedByBatchSize_AndIdempotentOnceNothingIsLeftToPurge()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		DateTime now = DateTime.UtcNow;
		for (int i = 0; i < 5; i++)
		{
			db.SyncRefreshTokenRotations.Add(NewRotation(session.Id, TestData.NewId($"op-{i}"), now.AddMinutes(-1)));
		}
		await db.SaveChangesAsync();

		ReplayPayloadCleanupService cleanup = new();
		int firstBatch = await cleanup.PurgeExpiredAsync(db, now, batchSize: 2);
		Assert.Equal(2, firstBatch);

		int remaining = 0;
		int purgedThisCall;
		do
		{
			purgedThisCall = await cleanup.PurgeExpiredAsync(db, now, batchSize: 2);
			remaining += purgedThisCall;
		}
		while (purgedThisCall > 0);
		Assert.Equal(3, remaining); // the other 3 of the original 5, in however many bounded batches it took.

		// A further call finds nothing left eligible - purging is idempotent, not an error, on an
		// already-fully-purged backlog.
		Assert.Equal(0, await cleanup.PurgeExpiredAsync(db, now, batchSize: 2));
	}
}
