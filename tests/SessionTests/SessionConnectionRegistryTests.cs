using Services.Sessions.Models;
using Services.Sessions.Service;
using Xunit;

namespace SessionTests;

/// <summary>Pure in-memory unit tests - no DB, no SignalR - for the registry real per-session disconnect depends on (see ISessionConnectionRegistry's own doc comment).</summary>
public class SessionConnectionRegistryTests
{
	private static LiveSessionConnection Connection(string connectionId, string sessionId, Action abort, string accountId = "acct-1", string deviceId = "dev-1", long trustVersion = 1) =>
		new(connectionId, accountId, sessionId, deviceId, trustVersion, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow, abort);

	[Fact]
	public void AbortSession_AbortsEveryConnectionRegisteredForThatSession_AndNoOthers()
	{
		SessionConnectionRegistry registry = new();
		int abortedForTarget = 0;
		int abortedForOther = 0;

		registry.Register(Connection("conn-1", "sess-target", () => abortedForTarget++));
		registry.Register(Connection("conn-2", "sess-target", () => abortedForTarget++)); // a second device tab/connection for the same session.
		registry.Register(Connection("conn-3", "sess-other", () => abortedForOther++));

		int aborted = registry.AbortSession("sess-target");

		Assert.Equal(2, aborted);
		Assert.Equal(2, abortedForTarget);
		Assert.Equal(0, abortedForOther);
	}

	[Fact]
	public void AbortSession_ForASessionWithNoRegisteredConnections_IsANoOp()
	{
		SessionConnectionRegistry registry = new();
		Assert.Equal(0, registry.AbortSession("never-registered"));
	}

	[Fact]
	public void Unregister_RemovesTheConnection_SoALaterAbortSessionNoLongerReachesIt()
	{
		SessionConnectionRegistry registry = new();
		int aborted = 0;
		registry.Register(Connection("conn-1", "sess-1", () => aborted++));

		registry.Unregister("conn-1");

		Assert.Equal(0, registry.AbortSession("sess-1"));
		Assert.Equal(0, aborted);
	}

	[Fact]
	public void Unregister_OnAConnectionIdThatWasNeverRegistered_IsSafe()
	{
		SessionConnectionRegistry registry = new();
		registry.Unregister("never-registered"); // must not throw.
	}

	[Fact]
	public void Snapshot_ReflectsCurrentlyRegisteredConnectionsOnly()
	{
		SessionConnectionRegistry registry = new();
		registry.Register(Connection("conn-1", "sess-1", () => { }, trustVersion: 7));
		registry.Register(Connection("conn-2", "sess-2", () => { }, trustVersion: 9));
		registry.Unregister("conn-1");

		var snapshot = registry.Snapshot();

		Assert.Single(snapshot);
		Assert.Equal("conn-2", snapshot.Single().ConnectionId);
		Assert.Equal(9, snapshot.Single().TrustVersionAtConnect);
	}

	[Fact]
	public void Register_TwiceForTheSameConnectionId_ReplacesRatherThanDuplicates()
	{
		// A reconnect reusing the same ConnectionId (not realistic for SignalR, which mints a fresh one
		// per connection, but the registry's own contract should not silently double-count regardless).
		SessionConnectionRegistry registry = new();
		int abortedOld = 0;
		int abortedNew = 0;
		registry.Register(Connection("conn-1", "sess-1", () => abortedOld++));
		registry.Register(Connection("conn-1", "sess-1", () => abortedNew++));

		int aborted = registry.AbortSession("sess-1");

		Assert.Equal(1, aborted);
		Assert.Equal(0, abortedOld);
		Assert.Equal(1, abortedNew);
	}

	[Fact]
	public void AbortSession_WhenOneConnectionsAbortThrows_StillAbortsEveryOtherConnection()
	{
		// Review3's blocking finding: a per-connection Abort() failure must never skip any other
		// connection, for THIS session or - since AbortSession is called once per session in a multi-session
		// revoke batch - implicitly never leave the caller unable to move on to the next session either.
		SessionConnectionRegistry registry = new();
		int abortedOther = 0;
		registry.Register(Connection("conn-throws", "sess-target", () => throw new InvalidOperationException("simulated transport failure")));
		registry.Register(Connection("conn-ok", "sess-target", () => abortedOther++));

		int aborted = registry.AbortSession("sess-target"); // must not throw.

		Assert.Equal(2, aborted);
		Assert.Equal(1, abortedOther);
	}

	[Fact]
	public async Task ConcurrentRegisterAndUnregisterForTheSameSession_NeverOrphansALiveConnectionFromAbortSession()
	{
		// The exact interleaving a two-index design (one ConcurrentDictionary<ConnectionId,...> plus a
		// second ConcurrentDictionary<SessionId, ConnectionIds> side index) could lose: a concurrent
		// Register for the session landing between the other index's "bucket looks empty" check and its
		// removal could silently orphan the newly-registered connection from that second index, making it
		// unreachable from AbortSession despite still being live in the first. With one authoritative
		// dictionary (see SessionConnectionRegistry's own doc comment) there is no second index to go
		// stale, so this must hold on EVERY iteration, deterministically, not merely "usually".
		SessionConnectionRegistry registry = new();
		const string sessionId = "sess-race";
		const int iterations = 500;

		for (int i = 0; i < iterations; i++)
		{
			int abortedA = 0;
			int abortedB = 0;
			string connA = $"conn-a-{i}";
			string connB = $"conn-b-{i}";
			registry.Register(Connection(connA, sessionId, () => Interlocked.Increment(ref abortedA)));

			Task unregisterA = Task.Run(() => registry.Unregister(connA));
			Task registerB = Task.Run(() => registry.Register(Connection(connB, sessionId, () => Interlocked.Increment(ref abortedB))));
			await Task.WhenAll(unregisterA, registerB);

			registry.AbortSession(sessionId);

			// connB was registered concurrently with connA's unregister - regardless of which ran first,
			// it must always still be reachable and aborted exactly once; connA must never be (double)
			// aborted after its own Unregister, regardless of ordering against AbortSession.
			Assert.Equal(1, abortedB);
			Assert.Equal(0, abortedA);

			registry.Unregister(connB);
		}
	}
}
