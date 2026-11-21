using Database.Context;
using Database.Enums.Sync;
using Services.Sessions;
using Services.Sessions.Service;
using Services.Sync.Contracts;
using Xunit;

namespace SessionTests;

[Collection(DatabaseCollection.Name)]
public class DeviceChallengeServiceTests(DatabaseFixture fixture)
{
	[Fact]
	public async Task IssueThenConsume_SucceedsExactlyOnce()
	{
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		DeviceChallengeService service = new(clock);

		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		string deviceId = TestData.NewId("dev");

		DeviceChallenge challenge = await service.IssueEnrollmentChallengeAsync(db, account.Id, deviceId, "key-1", spki, Database.Enums.Sync.ChallengePurpose.Enrollment);
		Assert.Equal(account.Id, challenge.AccountContextId);
		Assert.Equal(deviceId, challenge.DeviceRegistrationId);

		await using AppDbContext db2 = fixture.CreateContext();
		var consumed = await service.ConsumeAsync(db2, challenge.ChallengeId);
		Assert.Equal(challenge.ChallengeId, consumed.Id);

		// Second consumption of the exact same challenge must fail - one-use.
		await using AppDbContext db3 = fixture.CreateContext();
		await Assert.ThrowsAsync<ChallengeInvalidException>(() => service.ConsumeAsync(db3, challenge.ChallengeId));
	}

	[Fact]
	public async Task Consume_RejectsAnExpiredChallenge()
	{
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		DeviceChallengeService service = new(clock);

		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();

		DeviceChallenge challenge = await service.IssueEnrollmentChallengeAsync(db, account.Id, TestData.NewId("dev"), "key-1", spki, Database.Enums.Sync.ChallengePurpose.Enrollment);

		clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));

		await using AppDbContext db2 = fixture.CreateContext();
		await Assert.ThrowsAsync<ChallengeInvalidException>(() => service.ConsumeAsync(db2, challenge.ChallengeId));
	}

	[Fact]
	public async Task Consume_RejectsAnUnknownChallengeId()
	{
		DeviceChallengeService service = new(TimeProvider.System);
		await using AppDbContext db = fixture.CreateContext();
		await Assert.ThrowsAsync<ChallengeInvalidException>(() => service.ConsumeAsync(db, "does-not-exist"));
	}

	[Fact]
	public async Task Consume_ConcurrentDoubleConsumption_OnlyOneWinner()
	{
		// Direct proof of the atomic conditional UPDATE's race-safety: two independent AppDbContext
		// instances (simulating two concurrent requests) racing to consume the SAME challenge. Exactly
		// one must succeed.
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		DeviceChallengeService service = new(clock);

		await using AppDbContext seedDb = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(seedDb);
		var (spki, _) = TestData.NewDeviceKey();
		DeviceChallenge challenge = await service.IssueEnrollmentChallengeAsync(seedDb, account.Id, TestData.NewId("dev"), "key-1", spki, Database.Enums.Sync.ChallengePurpose.Enrollment);

		await using AppDbContext dbA = fixture.CreateContext();
		await using AppDbContext dbB = fixture.CreateContext();

		Task<Database.Entities.Sync.SyncDeviceChallengeEntity> taskA = service.ConsumeAsync(dbA, challenge.ChallengeId);
		Task<Database.Entities.Sync.SyncDeviceChallengeEntity> taskB = service.ConsumeAsync(dbB, challenge.ChallengeId);

		var results = await Task.WhenAll(
			taskA.ContinueWith(t => t.IsCompletedSuccessfully),
			taskB.ContinueWith(t => t.IsCompletedSuccessfully));

		Assert.Equal(1, results.Count(r => r));
	}
}
