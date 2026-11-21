using Microsoft.EntityFrameworkCore;
using Database.Context;
using Database.Enums.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Xunit;

namespace SessionTests;

/// <summary>
/// Exercises <see cref="SessionMaintenanceHostedService.RunOneMaintenanceTickAsync"/> directly (that
/// method's own doc comment explains why it is public: so these tests never wait on the real 15s
/// <c>PeriodicTimer</c>). Uses a small hand-rolled <see cref="IServiceScopeFactory"/> (no DI container
/// package assumed beyond the <c>Microsoft.Extensions.DependencyInjection.Abstractions</c> types
/// Services.csproj/BaseHub.cs already use throughout) so each simulated "tick" gets a scope resolving a
/// real, fixture-backed <see cref="AppDbContext"/> and a real <see cref="ReplayPayloadCleanupService"/>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SessionMaintenanceHostedServiceTests(DatabaseFixture fixture)
{
	private sealed class ThrowingReplayPayloadCleanupService : Services.Sessions.Interface.IReplayPayloadCleanupService
	{
		public Task<int> PurgeExpiredAsync(AppDbContext db, DateTime now, int batchSize, CancellationToken ct = default) =>
			throw new InvalidOperationException("simulated replay-cleanup failure");
	}

	/// <summary>Deliberately ignores `ct` entirely - the exact "uncooperative callee" scenario the service's own `Task.WhenAny` deadline race (not merely a `CancellationToken` handed to a potentially-uncooperative task) exists to bound.</summary>
	private sealed class NeverCompletingReplayPayloadCleanupService : Services.Sessions.Interface.IReplayPayloadCleanupService
	{
		public int Invocations;

		public async Task<int> PurgeExpiredAsync(AppDbContext db, DateTime now, int batchSize, CancellationToken ct = default)
		{
			Invocations++;
			await new TaskCompletionSource<int>().Task; // never completes.
			return 0; // unreachable.
		}
	}

	/// <summary>Never completes on its own - the test drives completion (success or fault) explicitly via <see cref="Completion"/>, to deterministically reproduce "an abandoned operation's Task eventually completes faulted" without racing real wall-clock timing for both halves of that scenario.</summary>
	private sealed class ControllableReplayPayloadCleanupService : Services.Sessions.Interface.IReplayPayloadCleanupService
	{
		public readonly TaskCompletionSource<int> Completion = new();
		public int Invocations;

		public Task<int> PurgeExpiredAsync(AppDbContext db, DateTime now, int batchSize, CancellationToken ct = default)
		{
			Invocations++;
			return Completion.Task;
		}
	}

	private sealed class RecordingLogger<T> : ILogger<T>
	{
		public readonly List<(LogLevel Level, Exception? Exception)> Entries = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
			Entries.Add((logLevel, exception));
	}

	private sealed class FakeServiceProvider(AppDbContext db, Services.Sessions.Interface.IReplayPayloadCleanupService cleanup) : IServiceProvider
	{
		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(AppDbContext)) return db;
			if (serviceType == typeof(Services.Sessions.Interface.IReplayPayloadCleanupService)) return cleanup;
			throw new NotSupportedException($"Unexpected service requested in this test fake: {serviceType}");
		}
	}

	private sealed class FakeServiceScope(AppDbContext db, Services.Sessions.Interface.IReplayPayloadCleanupService cleanup) : IServiceScope
	{
		public IServiceProvider ServiceProvider { get; } = new FakeServiceProvider(db, cleanup);

		public void Dispose() => db.Dispose();
	}

	private sealed class FakeServiceScopeFactory(Func<AppDbContext> dbFactory, Func<Services.Sessions.Interface.IReplayPayloadCleanupService>? cleanupFactory = null) : IServiceScopeFactory
	{
		public IServiceScope CreateScope() => new FakeServiceScope(dbFactory(), (cleanupFactory ?? (() => new ReplayPayloadCleanupService()))());
	}

	private LiveSessionConnection Connection(
		string connectionId, string accountId, string sessionId, string deviceId, long trustVersionAtConnect,
		DateTimeOffset expiresAtUtc, Action? abort = null) =>
		new(connectionId, accountId, sessionId, deviceId, trustVersionAtConnect, expiresAtUtc, DateTimeOffset.UtcNow, abort ?? (() => { }));

	[Fact]
	public async Task RunOneMaintenanceTickAsync_AbortsAnIdleConnectionWhoseAccessTokenHasExpired_EvenThoughNothingWasRevoked()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		SessionConnectionRegistry registry = new();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		int aborted = 0;
		registry.Register(Connection("conn-1", account.Id, session.Id, device.Id, device.TrustVersion, clock.GetUtcNow().AddSeconds(-1), () => aborted++));

		SessionMaintenanceHostedService service = new(
			new FakeServiceScopeFactory(fixture.CreateContext), registry, clock, NullLogger<SessionMaintenanceHostedService>.Instance);

		await service.RunOneMaintenanceTickAsync(CancellationToken.None);

		Assert.Equal(1, aborted);
		Assert.Empty(registry.Snapshot());
	}

	[Fact]
	public async Task RunOneMaintenanceTickAsync_AbortsAConnectionWhoseDeviceWasRevokedSinceConnect()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");
		long trustVersionAtConnect = device.TrustVersion;

		device.TrustState = DeviceTrustState.Revoked;
		device.TrustVersion += 1;
		await db.SaveChangesAsync();

		SessionConnectionRegistry registry = new();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		int aborted = 0;
		registry.Register(Connection("conn-1", account.Id, session.Id, device.Id, trustVersionAtConnect, clock.GetUtcNow().AddHours(1), () => aborted++));

		SessionMaintenanceHostedService service = new(
			new FakeServiceScopeFactory(fixture.CreateContext), registry, clock, NullLogger<SessionMaintenanceHostedService>.Instance);

		await service.RunOneMaintenanceTickAsync(CancellationToken.None);

		Assert.Equal(1, aborted);
		Assert.Empty(registry.Snapshot());
	}

	[Fact]
	public async Task RunOneMaintenanceTickAsync_LeavesAStillTrustedConnectionRegisteredAndNotAborted()
	{
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		SessionConnectionRegistry registry = new();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		int aborted = 0;
		registry.Register(Connection("conn-1", account.Id, session.Id, device.Id, device.TrustVersion, clock.GetUtcNow().AddHours(1), () => aborted++));

		SessionMaintenanceHostedService service = new(
			new FakeServiceScopeFactory(fixture.CreateContext), registry, clock, NullLogger<SessionMaintenanceHostedService>.Instance);

		await service.RunOneMaintenanceTickAsync(CancellationToken.None);

		Assert.Equal(0, aborted);
		Assert.Single(registry.Snapshot());
	}

	[Fact]
	public async Task RunOneMaintenanceTickAsync_OnASustainedDatabaseOutage_LeavesAConnectionAloneUntilTheFailClosedBoundIsReached()
	{
		// A disposed AppDbContext throws ObjectDisposedException on any query - a real, not simulated,
		// failure mode standing in for "the database is unreachable" without needing a fake provider.
		AppDbContext poisonedDb = fixture.CreateContext();
		await poisonedDb.DisposeAsync();

		SessionConnectionRegistry registry = new();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		int aborted = 0;
		registry.Register(Connection("conn-1", "acct-1", "sess-1", "dev-1", 1, clock.GetUtcNow().AddHours(1), () => aborted++));

		SessionMaintenanceHostedService service = new(
			new FakeServiceScopeFactory(() => poisonedDb), registry, clock, NullLogger<SessionMaintenanceHostedService>.Instance);

		// First tick, right after connect: the DB query fails, but the connection's own ConnectedAtUtc is
		// "now" - comfortably inside the fail-closed bound - so it must be left alone, not aborted merely
		// because of one failed tick.
		await service.RunOneMaintenanceTickAsync(CancellationToken.None);
		Assert.Equal(0, aborted);
		Assert.Single(registry.Snapshot());

		// Advance well past the fail-closed bound while the outage continues - this tick must now abort
		// it: a connection this replica can never re-verify must not be trusted indefinitely.
		clock.Advance(TimeSpan.FromSeconds(90));
		await service.RunOneMaintenanceTickAsync(CancellationToken.None);
		Assert.Equal(1, aborted);
		Assert.Empty(registry.Snapshot());
	}

	[Fact]
	public async Task RunOneMaintenanceTickAsync_AReplayCleanupFailure_StillAllowsConnectionRevalidationThatSameTickToRun()
	{
		// Connection revalidation (run FIRST, in its own try/catch - see this class's own doc comment)
		// must still take effect even though the replay-cleanup half of this SAME tick is forced to throw
		// via this fake IReplayPayloadCleanupService - proving the two are genuinely independent failure
		// domains, not merely reordered.
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		long trustVersionAtConnect = device.TrustVersion;
		device.TrustState = DeviceTrustState.Revoked;
		await db.SaveChangesAsync();
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		SessionConnectionRegistry registry = new();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		int aborted = 0;
		registry.Register(Connection("conn-1", account.Id, session.Id, device.Id, trustVersionAtConnect, clock.GetUtcNow().AddHours(1), () => aborted++));

		SessionMaintenanceHostedService service = new(
			new FakeServiceScopeFactory(fixture.CreateContext, () => new ThrowingReplayPayloadCleanupService()),
			registry, clock, NullLogger<SessionMaintenanceHostedService>.Instance);

		// Must not throw - RunOneMaintenanceTickAsync itself catches and logs the cleanup failure.
		await service.RunOneMaintenanceTickAsync(CancellationToken.None);

		Assert.Equal(1, aborted);
		Assert.Empty(registry.Snapshot());
	}

	[Fact]
	public async Task RunOneMaintenanceTickAsync_WhenReplayCleanupNeverCompletesAndIgnoresCancellation_StillReturnsWithinTheDeadlineAndStillRevalidatesARevokedConnectionThatSameTick()
	{
		// Independent review finding: a shared tick try/catch does not bound a callee that simply never
		// returns and never observes cancellation - only an internally-owned Task.WhenAny deadline does.
		// This proves BOTH halves at once: (1) the hang is actually bounded (not awaited forever), and
		// (2) it never suppresses that SAME tick's own connection revalidation (independent failure domains).
		await using AppDbContext db = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(db);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(db, account, spki, "key-1");
		long trustVersionAtConnect = device.TrustVersion;
		device.TrustState = DeviceTrustState.Revoked;
		await db.SaveChangesAsync();
		var instance = await TestData.CreateLocalInstanceAsync(db, device);
		var session = await TestData.CreateSessionAsync(db, account, device, instance, "raw-token-1");

		SessionConnectionRegistry registry = new();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		int aborted = 0;
		registry.Register(Connection("conn-1", account.Id, session.Id, device.Id, trustVersionAtConnect, clock.GetUtcNow().AddHours(1), () => aborted++));

		NeverCompletingReplayPayloadCleanupService hangingCleanup = new();
		SessionMaintenanceHostedService service = new(
			new FakeServiceScopeFactory(fixture.CreateContext, () => hangingCleanup),
			registry, clock, NullLogger<SessionMaintenanceHostedService>.Instance);

		System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
		await service.RunOneMaintenanceTickAsync(CancellationToken.None);
		stopwatch.Stop();

		// Bounded by the service's own internal DbOperationTimeout (10s) - comfortably inside this test's
		// own generous ceiling, never left hanging indefinitely on the never-completing cleanup call.
		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"RunOneMaintenanceTickAsync took {stopwatch.Elapsed} - a hung, cancellation-ignoring cleanup must be abandoned, not awaited forever.");
		Assert.Equal(1, hangingCleanup.Invocations);
		// The revoked device's connection was still aborted THIS SAME TICK - cleanup's hang never
		// suppressed the independent connection-revalidation half.
		Assert.Equal(1, aborted);
		Assert.Empty(registry.Snapshot());

		// A second tick must not start a second overlapping cleanup call while the first abandoned one has
		// still not returned - bounds resource usage instead of leaking a new hung call every tick.
		await service.RunOneMaintenanceTickAsync(CancellationToken.None);
		Assert.Equal(1, hangingCleanup.Invocations);
	}

	[Fact]
	public async Task RunOneMaintenanceTickAsync_WhenTheRevalidationQueryGenuinelyHangs_StillReturnsWithinTheDeadlineAndNeverUsesTheLateResultToRestoreAbortedTrust()
	{
		// A real Postgres ACCESS EXCLUSIVE table lock, held open by a separate, never-committed
		// transaction - a genuine way to make the revalidation SELECT actually block for real wall-clock
		// time (a plain SELECT needs ACCESS SHARE, which conflicts with ACCESS EXCLUSIVE), proving the
		// service's own deadline is an enforced await bound, not merely a CancellationToken the query might
		// never observe.
		await using AppDbContext seedDb = fixture.CreateContext();
		var account = await TestData.CreateAccountAsync(seedDb);
		var (spki, _) = TestData.NewDeviceKey();
		var device = await TestData.CreateTrustedDeviceAsync(seedDb, account, spki, "key-1");
		var instance = await TestData.CreateLocalInstanceAsync(seedDb, device);
		var session = await TestData.CreateSessionAsync(seedDb, account, device, instance, "raw-token-1");

		await using AppDbContext lockDb = fixture.CreateContext();
		await using var lockTx = await lockDb.Database.BeginTransactionAsync();
		await lockDb.Database.ExecuteSqlRawAsync("""LOCK TABLE "SyncDeviceSessions" IN ACCESS EXCLUSIVE MODE""");

		SessionConnectionRegistry registry = new();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		int aborted = 0;
		registry.Register(Connection("conn-1", account.Id, session.Id, device.Id, device.TrustVersion, clock.GetUtcNow().AddHours(1), () => aborted++));

		SessionMaintenanceHostedService service = new(
			new FakeServiceScopeFactory(fixture.CreateContext), registry, clock, NullLogger<SessionMaintenanceHostedService>.Instance);

		System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
		await service.RunOneMaintenanceTickAsync(CancellationToken.None);
		stopwatch.Stop();

		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"RunOneMaintenanceTickAsync took {stopwatch.Elapsed} - the hung query must be abandoned, not awaited to completion.");
		// Still well inside MaxUnverifiedAge's 60s grace window on this connection's very first tick - left
		// alone, not aborted merely because of one hung attempt.
		Assert.Equal(0, aborted);
		Assert.Single(registry.Snapshot());

		// A second tick, with the lock STILL held, must not start a second overlapping query.
		clock.Advance(TimeSpan.FromSeconds(30));
		await service.RunOneMaintenanceTickAsync(CancellationToken.None);
		Assert.Equal(0, aborted);

		// Advance the fake clock past MaxUnverifiedAge while the real hang continues - fails closed.
		clock.Advance(TimeSpan.FromSeconds(40));
		await service.RunOneMaintenanceTickAsync(CancellationToken.None);
		Assert.Equal(1, aborted);
		Assert.Empty(registry.Snapshot());

		// Release the lock - the long-abandoned query can now finally complete, with a TRUE "still trusted"
		// answer (nothing was ever actually revoked). Its late result must never resurrect a connection
		// this replica already gave up on and removed.
		await lockTx.RollbackAsync();
		await Task.Delay(TimeSpan.FromSeconds(1));
		await service.RunOneMaintenanceTickAsync(CancellationToken.None);
		Assert.Equal(1, aborted);
		Assert.Empty(registry.Snapshot());
	}

	[Fact]
	public async Task RunOneMaintenanceTickAsync_WhenAnAbandonedCleanupLaterCompletesFaulted_LogsTheFaultOnceObserved()
	{
		// Review3's non-blocking finding: a previously-abandoned operation that eventually completes FAULTED
		// (not merely late-but-successful) was silently discarded - never observed/logged at all. The
		// fail-closed path already ran at abandon-time regardless (this is a diagnostics-only gap), but a
		// genuine fault must still be visible once this class does get around to disposing that operation.
		SessionConnectionRegistry registry = new();
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		ControllableReplayPayloadCleanupService cleanup = new();
		RecordingLogger<SessionMaintenanceHostedService> logger = new();

		SessionMaintenanceHostedService service = new(
			new FakeServiceScopeFactory(fixture.CreateContext, () => cleanup), registry, clock, logger);

		// First tick: the cleanup call never completes within the real (not test-configurable)
		// DbOperationTimeout, so it is abandoned and its Task is kept for a later tick to observe.
		await service.RunOneMaintenanceTickAsync(CancellationToken.None);
		Assert.Equal(1, cleanup.Invocations);

		// The abandoned operation now actually completes, but FAULTED.
		cleanup.Completion.SetException(new InvalidOperationException("simulated late cleanup failure"));

		// A second tick finds the abandoned Task completed and must log its fault before discarding it.
		await service.RunOneMaintenanceTickAsync(CancellationToken.None);

		Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is AggregateException agg && agg.InnerException is InvalidOperationException);
	}
}
