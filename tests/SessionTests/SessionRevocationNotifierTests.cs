using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Services.Hubs;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Xunit;
using WireContracts = Services.Sync.Contracts;

namespace SessionTests;

/// <summary>
/// Proves real enforcement (<see cref="ISessionConnectionRegistry.AbortSession"/>) is independent of the
/// best-effort courtesy SignalR send: a failing, hanging, or canceled-via-the-caller's-own-token send
/// must never skip, delay past a bound, or fail the abort - see SessionRevocationNotifier's own doc
/// comment. Uses a hand-rolled <see cref="IHubContext{BaseHub}"/>/<see cref="IHubClients"/>/
/// <see cref="IClientProxy"/> (no mocking library is referenced anywhere in this solution) so the
/// courtesy send's exact failure mode is fully controlled.
/// </summary>
public class SessionRevocationNotifierTests
{
	private sealed class ThrowingClientProxy : IClientProxy
	{
		public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException("simulated SignalR transport failure");
	}

	private sealed class HangingClientProxy : IClientProxy
	{
		public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
			Task.Delay(Timeout.Infinite, cancellationToken);
	}

	/// <summary>Never observes cancellation at all (unlike <see cref="HangingClientProxy"/>, which - as a well-behaved <c>Task.Delay</c> consumer - does) - the exact "transport does not honor the token" failure mode this class's own `Task.WhenAny`-against-an-owned-`Task.Delay` bound must survive regardless of the token passed to `SendAsync`.</summary>
	private sealed class UncooperativeHangingClientProxy : IClientProxy
	{
		public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
			Task.Delay(Timeout.Infinite, CancellationToken.None);
	}

	private sealed class OrderRecordingClientProxy(ConcurrentQueue<string> order) : IClientProxy
	{
		public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
		{
			order.Enqueue("send:" + args[0]);
			return Task.CompletedTask;
		}
	}

	private sealed class DelayingClientProxy(TimeSpan delay) : IClientProxy
	{
		public async Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
			await Task.Delay(delay, CancellationToken.None);
	}

	private sealed class FakeHubClients(IClientProxy proxy) : IHubClients
	{
		public IClientProxy All => proxy;
		public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => proxy;
		public IClientProxy Client(string connectionId) => proxy;
		public IClientProxy Clients(IReadOnlyList<string> connectionIds) => proxy;
		public IClientProxy Group(string groupName) => proxy;
		public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => proxy;
		public IClientProxy Groups(IReadOnlyList<string> groupNames) => proxy;
		public IClientProxy User(string userId) => proxy;
		public IClientProxy Users(IReadOnlyList<string> userIds) => proxy;
	}

	private sealed class FakeHubContext(IClientProxy proxy) : IHubContext<BaseHub>
	{
		public IHubClients Clients { get; } = new FakeHubClients(proxy);
		public IGroupManager Groups => throw new NotSupportedException("Not needed by SessionRevocationNotifier.");
	}

	private static LiveSessionConnection Connection(string connectionId, string sessionId, Action abort) =>
		new(connectionId, "acct-1", sessionId, "dev-1", 1, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow, abort);

	[Fact]
	public async Task NotifySessionRevokedAsync_AbortsRegisteredConnections_EvenWhenTheCourtesySendThrows()
	{
		SessionConnectionRegistry registry = new();
		int aborted = 0;
		registry.Register(Connection("conn-1", "sess-1", () => aborted++));

		SessionRevocationNotifier notifier = new(new FakeHubContext(new ThrowingClientProxy()), registry, NullLogger<SessionRevocationNotifier>.Instance);

		// Must not throw - a failed best-effort courtesy notice must never surface as a failure of, or
		// skip, the real enforcement below.
		await notifier.NotifySessionRevokedAsync("sess-1", WireContracts.SessionControlReason.Revoked);

		Assert.Equal(1, aborted);
	}

	[Fact]
	public async Task NotifySessionRevokedAsync_AbortsRegisteredConnections_EvenWhenTheCourtesySendHangsIndefinitely()
	{
		SessionConnectionRegistry registry = new();
		int aborted = 0;
		registry.Register(Connection("conn-1", "sess-1", () => aborted++));

		SessionRevocationNotifier notifier = new(new FakeHubContext(new HangingClientProxy()), registry, NullLogger<SessionRevocationNotifier>.Instance);

		// Bounded by the notifier's own internal courtesy-send timeout, not by this test's patience - a
		// non-responding transport must not hold up the real abort.
		Task notifyTask = notifier.NotifySessionRevokedAsync("sess-1", WireContracts.SessionControlReason.Revoked);
		Task first = await Task.WhenAny(notifyTask, Task.Delay(TimeSpan.FromSeconds(15)));
		Assert.Same(notifyTask, first);
		await notifyTask;

		Assert.Equal(1, aborted);
	}

	[Fact]
	public async Task NotifySessionRevokedAsync_StillEnforces_WhenTheCallersOwnTokenIsAlreadyCanceled()
	{
		// Simulates the HTTP request that triggered the revoke being canceled (e.g. the client
		// disconnected) strictly AFTER the revoking transaction already committed - enforcement must not
		// depend on that caller's own token surviving to this point.
		SessionConnectionRegistry registry = new();
		int aborted = 0;
		registry.Register(Connection("conn-1", "sess-1", () => aborted++));

		SessionRevocationNotifier notifier = new(new FakeHubContext(new ThrowingClientProxy()), registry, NullLogger<SessionRevocationNotifier>.Instance);
		using CancellationTokenSource alreadyCanceled = new();
		alreadyCanceled.Cancel();

		await notifier.NotifySessionRevokedAsync("sess-1", WireContracts.SessionControlReason.Revoked, alreadyCanceled.Token);

		Assert.Equal(1, aborted);
	}

	[Fact]
	public async Task NotifySessionRevokedAsync_ForASessionWithNoRegisteredConnectionsOnThisReplica_IsSafe()
	{
		SessionConnectionRegistry registry = new();
		SessionRevocationNotifier notifier = new(new FakeHubContext(new ThrowingClientProxy()), registry, NullLogger<SessionRevocationNotifier>.Instance);

		await notifier.NotifySessionRevokedAsync("never-registered", WireContracts.SessionControlReason.Revoked);
	}

	[Fact]
	public async Task NotifySessionRevokedAsync_AbortsRegisteredConnections_EvenWhenTheCourtesySendNeverObservesCancellationAtAll()
	{
		// The exact failure mode review3 flagged: a transport that does not honor the CancellationToken
		// passed to SendAsync at all (not even eventually) must still never hold up enforcement, and this
		// class's own bound must come from an internally-owned Task.Delay, never from trusting the token.
		SessionConnectionRegistry registry = new();
		int aborted = 0;
		registry.Register(Connection("conn-1", "sess-1", () => aborted++));

		SessionRevocationNotifier notifier = new(new FakeHubContext(new UncooperativeHangingClientProxy()), registry, NullLogger<SessionRevocationNotifier>.Instance);

		Task notifyTask = notifier.NotifySessionRevokedAsync("sess-1", WireContracts.SessionControlReason.Revoked);
		Task first = await Task.WhenAny(notifyTask, Task.Delay(TimeSpan.FromSeconds(15)));
		Assert.Same(notifyTask, first);
		await notifyTask;

		Assert.Equal(1, aborted);
	}

	[Fact]
	public async Task NotifySessionRevokedAsync_AbortsLocalConnections_StrictlyBeforeAttemptingTheCourtesySend()
	{
		ConcurrentQueue<string> order = new();
		SessionConnectionRegistry registry = new();
		registry.Register(Connection("conn-1", "sess-1", () => order.Enqueue("abort:sess-1")));

		SessionRevocationNotifier notifier = new(new FakeHubContext(new OrderRecordingClientProxy(order)), registry, NullLogger<SessionRevocationNotifier>.Instance);
		await notifier.NotifySessionRevokedAsync("sess-1", WireContracts.SessionControlReason.Revoked);

		Assert.Equal(2, order.Count);
		Assert.Equal("abort:sess-1", order.First());
	}

	[Fact]
	public async Task NotifyAllSessionsRevokedAsync_AbortsEveryGivenSessionsLocalConnections_BeforeAnyOfThemAttemptsItsCourtesySend()
	{
		ConcurrentQueue<string> order = new();
		SessionConnectionRegistry registry = new();
		registry.Register(Connection("conn-1", "sess-1", () => order.Enqueue("abort:sess-1")));
		registry.Register(Connection("conn-2", "sess-2", () => order.Enqueue("abort:sess-2")));
		registry.Register(Connection("conn-3", "sess-3", () => order.Enqueue("abort:sess-3")));

		SessionRevocationNotifier notifier = new(new FakeHubContext(new OrderRecordingClientProxy(order)), registry, NullLogger<SessionRevocationNotifier>.Instance);
		await notifier.NotifyAllSessionsRevokedAsync(["sess-1", "sess-2", "sess-3"], WireContracts.SessionControlReason.Revoked);

		string[] events = order.ToArray();
		Assert.Equal(6, events.Length);
		int firstSendIndex = Array.FindIndex(events, e => e.StartsWith("send:", StringComparison.Ordinal));
		int lastAbortIndex = Array.FindLastIndex(events, e => e.StartsWith("abort:", StringComparison.Ordinal));
		// Every abort (for every session in the batch) happened before any courtesy send was even attempted
		// - not merely "this session's own abort before this session's own send", which a sequential
		// per-session loop would also satisfy.
		Assert.True(lastAbortIndex < firstSendIndex, $"Expected every abort before any send; order was: {string.Join(", ", events)}");
	}

	[Fact]
	public async Task NotifyAllSessionsRevokedAsync_SendsEverySessionsCourtesyNotice_ConcurrentlyNotSequentially()
	{
		// A per-session 3s sequential delay before the LAST session's own enforcement is exactly what
		// review3 flagged - this proves sessions' courtesy sends overlap in wall-clock time rather than
		// running one full attempt after another. Enforcement itself is unconditional/synchronous (see the
		// ordering test above) so this test only needs to measure the courtesy-send half.
		TimeSpan perSendDelay = TimeSpan.FromMilliseconds(400);
		SessionConnectionRegistry registry = new();
		SessionRevocationNotifier notifier = new(new FakeHubContext(new DelayingClientProxy(perSendDelay)), registry, NullLogger<SessionRevocationNotifier>.Instance);

		Stopwatch stopwatch = Stopwatch.StartNew();
		await notifier.NotifyAllSessionsRevokedAsync(["sess-1", "sess-2", "sess-3"], WireContracts.SessionControlReason.Revoked);
		stopwatch.Stop();

		// Three sequential 400ms sends would take >= 1200ms; concurrently they should all finish close to
		// one 400ms delay. A generous upper bound (well under the sequential sum) keeps this non-flaky.
		Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(1100), $"Expected concurrent sends well under 1200ms, took {stopwatch.ElapsedMilliseconds}ms.");
	}

	[Fact]
	public async Task NotifyAllSessionsRevokedAsync_WhenEverySessionsCourtesySendHangsIndefinitely_StillReturnsWithinOneBoundNotN()
	{
		SessionConnectionRegistry registry = new();
		int abortedCount = 0;
		registry.Register(Connection("conn-1", "sess-1", () => Interlocked.Increment(ref abortedCount)));
		registry.Register(Connection("conn-2", "sess-2", () => Interlocked.Increment(ref abortedCount)));
		registry.Register(Connection("conn-3", "sess-3", () => Interlocked.Increment(ref abortedCount)));

		SessionRevocationNotifier notifier = new(new FakeHubContext(new HangingClientProxy()), registry, NullLogger<SessionRevocationNotifier>.Instance);

		Task notifyTask = notifier.NotifyAllSessionsRevokedAsync(["sess-1", "sess-2", "sess-3"], WireContracts.SessionControlReason.Revoked);
		// A sequential per-session 3s bound for 3 sessions would take ~9s; concurrently bounded it must
		// finish close to ONE CourtesySendTimeout regardless of batch size.
		Task first = await Task.WhenAny(notifyTask, Task.Delay(TimeSpan.FromSeconds(8)));
		Assert.Same(notifyTask, first);
		await notifyTask;

		Assert.Equal(3, abortedCount);
	}
}
