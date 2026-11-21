using System.Collections.Concurrent;
using Database.Context;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using Services.Sync.Interface;

namespace Services.Sessions.Service;

/// <summary>
/// Three independent, small periodic jobs sharing one timer loop (not a god service - each does exactly
/// one thing, see the private methods below), per plan/05-device-sessions.md and plan/13-history-api.md:
///
/// 1. <see cref="RevalidateLiveConnectionsAsync"/> - the cross-replica half of real revocation
///    enforcement for idle hub connections. <see cref="SessionRevocationNotifier"/> already aborts
///    same-replica connections immediately (<see cref="ISessionConnectionRegistry.AbortSession"/>);
///    this sweep is what closes connections parked on a DIFFERENT replica than the one that processed
///    the revoke. It also enforces plain token expiry for an idle connection
///    (<see cref="LiveSessionConnection.ExpiresAtUtc"/>) - a DB-independent, always-available check - and
///    a fail-closed bound for a sustained DB outage/hang (<see cref="MaxUnverifiedAge"/>).
/// 2. <see cref="IReplayPayloadCleanupService.PurgeExpiredAsync"/> - bounded data-minimization cleanup
///    of expired encrypted refresh-replay payloads.
/// 3. <see cref="ISyncRetention"/> (stage 13) - expired sync sessions and one bounded portion of change-
///    journal pruning, at its own much slower cadence (<see cref="SyncRetentionInterval"/>).
///
/// PROPAGATION GUARANTEE, stated precisely: each tick runs the jobs in INDEPENDENT try/catch blocks, each
/// against its OWN <see cref="AppDbContext"/> scope (not a shared one) - a slow/failing/hung cleanup can
/// never suppress that tick's own connection sweep, and vice versa, and none can leave another
/// concurrently using a DbContext it does not own.
///
/// A plain <see cref="CancellationToken"/> is NOT by itself an upper bound here: an uncooperative wait
/// (connection-pool exhaustion, a genuinely stuck TCP read, a server-side lock wait) may simply never
/// observe cancellation promptly, or at all, until the connection itself is forcibly torn down. Both DB
/// operations below are therefore raced against an explicit, internally-owned wall-clock deadline
/// (<see cref="DbOperationTimeout"/>, via <c>Task.WhenAny</c> against <c>Task.Delay</c> - the same
/// "own the bound, don't merely hope the callee cooperates" shape <see cref="SessionRevocationNotifier"/>'s
/// own <c>CourtesySendTimeout</c> already established in this codebase). If the real operation does not
/// win that race, this tick ABANDONS it (never awaits it further, never uses its eventual result) and
/// falls back to the local, time-based <see cref="MaxUnverifiedAge"/> bound instead - a delayed/hung
/// query's eventual completion must never retroactively restore trust using a result that no longer
/// reflects "now". At most ONE abandoned attempt per operation kind is ever outstanding at a time (a new
/// one is never started while a prior tick's own abandoned attempt has still not returned) - this bounds
/// resource usage (pooled connections, scopes) to a constant even under a sustained hang, rather than
/// leaking a fresh hung query/scope every <see cref="PollInterval"/>. Because every tick is now bounded by
/// <see cref="DbOperationTimeout"/> regardless of whether the underlying I/O cooperates, <c>ExecuteAsync</c>'s
/// otherwise-strictly-sequential tick loop can never wedge permanently on a single hung tick - the
/// DB-independent token-expiry check and the <see cref="MaxUnverifiedAge"/> fail-closed bound both keep
/// running on every subsequent tick, exactly as intended, instead of only on tied-to-an-exception failures.
/// </summary>
public sealed class SessionMaintenanceHostedService(
	IServiceScopeFactory scopeFactory,
	ISessionConnectionRegistry registry,
	TimeProvider timeProvider,
	ILogger<SessionMaintenanceHostedService> logger) : BackgroundService
{
	/// <summary>Nominal interval between the START of consecutive ticks.</summary>
	private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

	/// <summary>Fail-closed bound for a sustained DB outage or hang: a connection this replica cannot re-verify against the DB for longer than this is aborted outright rather than trusted indefinitely. Enforced once per <see cref="PollInterval"/> tick, not continuously/instantly - the actual worst-case wall-clock time before a connection crossing this bound is actually aborted is this value PLUS up to one more <see cref="PollInterval"/>, never an exact-to-the-second real-time deadline.</summary>
	private static readonly TimeSpan MaxUnverifiedAge = TimeSpan.FromSeconds(60);

	/// <summary>Enforced wall-clock deadline for one revalidation query or one cleanup batch-loop - see this class's own doc comment for why this is a real <c>Task.WhenAny</c> race, not merely a <see cref="CancellationToken"/> handed to a potentially-uncooperative call.</summary>
	private static readonly TimeSpan DbOperationTimeout = TimeSpan.FromSeconds(10);

	/// <summary>How often the stage-13 sync-retention sweep runs - deliberately far slower than <see cref="PollInterval"/>, because its own horizons are measured in days.</summary>
	private static readonly TimeSpan SyncRetentionInterval = TimeSpan.FromMinutes(5);

	private const int ReplayPurgeBatchSize = 500;
	private const int ReplayPurgeMaxBatchesPerTick = 20; // bounds one tick's total work to at most 10,000 rows, regardless of backlog size.

	/// <summary>Per-connection last-successfully-DB-verified timestamp, for <see cref="MaxUnverifiedAge"/>. Pruned every tick for connections no longer registered.</summary>
	private readonly ConcurrentDictionary<string, DateTimeOffset> _lastVerifiedAtUtc = new();

	/// <summary>An operation this tick gave up waiting on - kept alive (never disposed/acted on) only so a later tick can release its scope once it finally does complete. Only ever read/written from within the sequential tick loop (<see cref="ExecuteAsync"/>/tests calling <see cref="RunOneMaintenanceTickAsync"/> directly), never concurrently with itself.</summary>
	private sealed record AbandonedOperation(AsyncServiceScope Scope, Task Task);

	private AbandonedOperation? _outstandingRevalidation;
	private AbandonedOperation? _outstandingCleanup;
	private AbandonedOperation? _outstandingRetention;

	/// <summary>When the sync-retention sweep last started; it runs far more slowly than the tick itself (see <see cref="RunSyncRetentionAsync"/>).</summary>
	private DateTimeOffset _lastSyncRetentionAtUtc = DateTimeOffset.MinValue;

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using PeriodicTimer timer = new(PollInterval);
		while (await timer.WaitForNextTickAsync(stoppingToken))
		{
			await RunOneMaintenanceTickAsync(stoppingToken);
		}
	}

	/// <summary>
	/// Does exactly one tick's worth of work. Public (not merely private, called only from
	/// <see cref="ExecuteAsync"/>'s timer loop) so tests can exercise this deterministically without
	/// waiting on the real <see cref="PollInterval"/>/<see cref="PeriodicTimer"/> - see
	/// SessionMaintenanceHostedServiceTests.cs. Always returns within roughly <see cref="DbOperationTimeout"/>
	/// times two plus local work, regardless of whether either underlying DB operation hangs - see this
	/// class's own doc comment.
	/// </summary>
	public async Task RunOneMaintenanceTickAsync(CancellationToken ct)
	{
		DateTimeOffset now = timeProvider.GetUtcNow();

		try
		{
			await RevalidateLiveConnectionsAsync(now, ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogError(ex, "SessionMaintenanceHostedService: live-connection revalidation failed this tick; will retry next tick.");
		}

		try
		{
			await RunReplayCleanupAsync(now, ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogError(ex, "SessionMaintenanceHostedService: replay-payload cleanup failed this tick; will retry next tick.");
		}

		try
		{
			await RunSyncRetentionAsync(now, ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogError(ex, "SessionMaintenanceHostedService: sync retention failed this tick; will retry next tick.");
		}
	}

	private async Task RevalidateLiveConnectionsAsync(DateTimeOffset now, CancellationToken ct)
	{
		IReadOnlyCollection<LiveSessionConnection> snapshot = registry.Snapshot();

		// Prune bookkeeping for connections that disconnected (and were unregistered) since the last tick.
		HashSet<string> liveConnectionIds = snapshot.Select(c => c.ConnectionId).ToHashSet();
		foreach (string trackedConnectionId in _lastVerifiedAtUtc.Keys)
		{
			if (!liveConnectionIds.Contains(trackedConnectionId))
			{
				_lastVerifiedAtUtc.TryRemove(trackedConnectionId, out _);
			}
		}

		if (snapshot.Count == 0)
		{
			return;
		}

		// Plain token expiry - local, DB-independent, always enforced regardless of DB reachability.
		List<LiveSessionConnection> stillLive = [];
		foreach (LiveSessionConnection connection in snapshot)
		{
			if (connection.ExpiresAtUtc <= now)
			{
				connection.Abort();
				registry.Unregister(connection.ConnectionId);
				_lastVerifiedAtUtc.TryRemove(connection.ConnectionId, out _);
			}
			else
			{
				stillLive.Add(connection);
			}
		}

		if (stillLive.Count == 0)
		{
			return;
		}

		if (_outstandingRevalidation is { } pending)
		{
			if (!pending.Task.IsCompleted)
			{
				// A PRIOR tick's query already exceeded DbOperationTimeout and still has not returned -
				// starting a second one would leak another hung query/pooled connection on top of the
				// first instead of bounding resource usage to one outstanding attempt at a time.
				logger.LogWarning("SessionMaintenanceHostedService: the previous live-connection revalidation query has still not returned; not starting another this tick.");
				ApplyFailClosedUnverifiedAgeBound(stillLive, now);
				return;
			}
			// It finally finished (late). Its scope is released now; its RESULT is still discarded
			// unconditionally - see this class's own doc comment for why a delayed answer must never
			// retroactively restore trust. A faulted (not merely late-but-successful) completion is still
			// worth observing for diagnostics, even though the fail-closed path already ran at abandon-time.
			ObserveAbandonedOperationFault(pending, "live-connection revalidation query");
			await pending.Scope.DisposeAsync();
			_outstandingRevalidation = null;
		}

		string[] sessionIds = stillLive.Select(c => c.SessionId).Distinct().ToArray();
		AsyncServiceScope queryScope = scopeFactory.CreateAsyncScope();
		Task<Dictionary<string, LiveConnectionDbRow>> queryTask = RunRevalidationQueryAsync(queryScope, sessionIds, ct);

		Task winner = await Task.WhenAny(queryTask, Task.Delay(DbOperationTimeout, ct));
		if (winner != queryTask)
		{
			logger.LogWarning(
				"SessionMaintenanceHostedService: live-connection revalidation query exceeded its {TimeoutSeconds}s bound; abandoning it and falling back to the fail-closed unverified-age bound for {Count} connection(s) this tick.",
				DbOperationTimeout.TotalSeconds, stillLive.Count);
			_outstandingRevalidation = new AbandonedOperation(queryScope, queryTask);
			ApplyFailClosedUnverifiedAgeBound(stillLive, now);
			return;
		}

		await queryScope.DisposeAsync();

		Dictionary<string, LiveConnectionDbRow> liveRows;
		try
		{
			liveRows = await queryTask;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogError(ex, "SessionMaintenanceHostedService: could not reach the database to revalidate {Count} live connection(s) this tick; falling back to the fail-closed unverified-age bound.", stillLive.Count);
			ApplyFailClosedUnverifiedAgeBound(stillLive, now);
			return;
		}

		foreach (LiveSessionConnection connection in stillLive)
		{
			// A session this replica has registered but the DB no longer has a row for at all is not
			// expected in normal operation, but fails closed (aborts) rather than assuming it is fine.
			bool stillTrusted = liveRows.TryGetValue(connection.SessionId, out LiveConnectionDbRow? row)
				&& row.EndedAtUtc is null
				&& row.AccountEndedAtUtc is null
				&& row.Device is not null
				&& row.Device.TrustState == DeviceTrustState.Trusted
				&& row.Device.TrustVersion == connection.TrustVersionAtConnect;

			if (!stillTrusted)
			{
				connection.Abort();
				registry.Unregister(connection.ConnectionId);
				_lastVerifiedAtUtc.TryRemove(connection.ConnectionId, out _);
			}
			else
			{
				_lastVerifiedAtUtc[connection.ConnectionId] = now;
			}
		}
	}

	private static Task<Dictionary<string, LiveConnectionDbRow>> RunRevalidationQueryAsync(AsyncServiceScope scope, string[] sessionIds, CancellationToken ct)
	{
		AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

		// One bulk query (not one per connection) - the live set this replica holds open is normally
		// small, and this still stays a single round trip regardless of its size. ONE correlated subquery
		// carrying both device fields together (not two separate FirstOrDefault() subqueries) keeps this a
		// single round trip per device, not two.
		return db.SyncDeviceSessions.AsNoTracking()
			.Where(s => sessionIds.Contains(s.Id))
			.Select(s => new LiveConnectionDbRow(
				s.Id,
				s.EndedAtUtc,
				db.SyncDeviceRegistrations
					.Where(d => d.AccountId == s.AccountId && d.Id == s.DeviceRegistrationId)
					.Select(d => new DeviceTrustRow(d.TrustState, d.TrustVersion))
					.FirstOrDefault(),
				db.SyncAccounts
					.Where(a => a.Id == s.AccountId)
					.Select(a => (DateTime?)a.EndedAtUtc)
					.FirstOrDefault()))
			.ToDictionaryAsync(s => s.SessionId, ct);
	}

	/// <summary>Logs (does not throw/act on) a previously-abandoned operation's eventual outcome if it completed FAULTED, not merely late-but-successful - a diagnostics-only gap: the fail-closed path already ran at abandon-time regardless, so this never changes behavior, only visibility.</summary>
	private void ObserveAbandonedOperationFault(AbandonedOperation pending, string operationName)
	{
		if (pending.Task.IsFaulted)
		{
			logger.LogError(pending.Task.Exception, "SessionMaintenanceHostedService: a previously-abandoned {OperationName} eventually completed, but faulted; its result was already discarded via the fail-closed bound applied when it was abandoned.", operationName);
		}
	}

	private void ApplyFailClosedUnverifiedAgeBound(List<LiveSessionConnection> stillLive, DateTimeOffset now)
	{
		foreach (LiveSessionConnection connection in stillLive)
		{
			DateTimeOffset lastVerified = _lastVerifiedAtUtc.GetValueOrDefault(connection.ConnectionId, connection.ConnectedAtUtc);
			if (now - lastVerified > MaxUnverifiedAge)
			{
				connection.Abort();
				registry.Unregister(connection.ConnectionId);
				_lastVerifiedAtUtc.TryRemove(connection.ConnectionId, out _);
			}
		}
	}

	private async Task RunReplayCleanupAsync(DateTimeOffset now, CancellationToken ct)
	{
		if (_outstandingCleanup is { } pending)
		{
			if (!pending.Task.IsCompleted)
			{
				logger.LogWarning("SessionMaintenanceHostedService: the previous replay-payload cleanup batch has still not returned; not starting another this tick.");
				return;
			}
			ObserveAbandonedOperationFault(pending, "replay-payload cleanup batch");
			await pending.Scope.DisposeAsync();
			_outstandingCleanup = null;
		}

		AsyncServiceScope cleanupScope = scopeFactory.CreateAsyncScope();
		Task cleanupTask = RunCleanupBatchesAsync(cleanupScope, now, ct);

		Task winner = await Task.WhenAny(cleanupTask, Task.Delay(DbOperationTimeout, ct));
		if (winner != cleanupTask)
		{
			logger.LogWarning("SessionMaintenanceHostedService: replay-payload cleanup exceeded its {TimeoutSeconds}s bound; abandoning it for this tick.", DbOperationTimeout.TotalSeconds);
			_outstandingCleanup = new AbandonedOperation(cleanupScope, cleanupTask);
			return;
		}

		await cleanupScope.DisposeAsync();
		await cleanupTask; // Already completed (the winner) - rethrows here if it faulted, caught by RunOneMaintenanceTickAsync's own try/catch.
	}

	/// <summary>
	/// Stage 13's bounded sync cleanup (<see cref="ISyncRetention"/>): expired sync sessions, then one
	/// portion of journal pruning. It runs on this same loop - a third small job rather than a third
	/// near-identical hosted service - but at its OWN, much slower cadence: a journal horizon is measured
	/// in days, so visiting it every <see cref="PollInterval"/> would be pure load. Same shape as the
	/// other two jobs: its own scope, its own wall-clock bound, and at most one abandoned attempt
	/// outstanding. Skipping a sweep is always safe; nothing depends on it having run.
	/// </summary>
	private async Task RunSyncRetentionAsync(DateTimeOffset now, CancellationToken ct)
	{
		if (_outstandingRetention is { } pending)
		{
			if (!pending.Task.IsCompleted)
			{
				logger.LogWarning("SessionMaintenanceHostedService: the previous sync-retention sweep has still not returned; not starting another this tick.");
				return;
			}

			ObserveAbandonedOperationFault(pending, "sync-retention sweep");
			await pending.Scope.DisposeAsync();
			_outstandingRetention = null;
		}

		if (now - _lastSyncRetentionAtUtc < SyncRetentionInterval)
		{
			return;
		}

		_lastSyncRetentionAtUtc = now;

		AsyncServiceScope retentionScope = scopeFactory.CreateAsyncScope();
		Task retentionTask = RunSyncRetentionSweepAsync(retentionScope, ct);

		Task winner = await Task.WhenAny(retentionTask, Task.Delay(DbOperationTimeout, ct));
		if (winner != retentionTask)
		{
			logger.LogWarning("SessionMaintenanceHostedService: sync retention exceeded its {TimeoutSeconds}s bound; abandoning it for this tick.", DbOperationTimeout.TotalSeconds);
			_outstandingRetention = new AbandonedOperation(retentionScope, retentionTask);
			return;
		}

		await retentionScope.DisposeAsync();
		await retentionTask; // Already completed (the winner) - rethrows here if it faulted, caught by RunOneMaintenanceTickAsync's own try/catch.
	}

	private static async Task RunSyncRetentionSweepAsync(AsyncServiceScope scope, CancellationToken ct)
	{
		ISyncRetention retention = scope.ServiceProvider.GetRequiredService<ISyncRetention>();
		await retention.PurgeExpiredSessionsAsync(ct);
		await retention.PruneJournalsAsync(ct);
	}

	private static async Task RunCleanupBatchesAsync(AsyncServiceScope scope, DateTimeOffset now, CancellationToken ct)
	{
		AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
		IReplayPayloadCleanupService cleanup = scope.ServiceProvider.GetRequiredService<IReplayPayloadCleanupService>();
		for (int i = 0; i < ReplayPurgeMaxBatchesPerTick; i++)
		{
			int purged = await cleanup.PurgeExpiredAsync(db, now.UtcDateTime, ReplayPurgeBatchSize, ct);
			if (purged < ReplayPurgeBatchSize)
			{
				break;
			}
		}
	}

	private sealed record DeviceTrustRow(DeviceTrustState TrustState, long TrustVersion);

	private sealed record LiveConnectionDbRow(
		string SessionId,
		DateTime? EndedAtUtc,
		DeviceTrustRow? Device,
		DateTime? AccountEndedAtUtc);
}
