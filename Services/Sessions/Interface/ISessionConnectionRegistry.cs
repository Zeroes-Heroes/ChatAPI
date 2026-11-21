using Services.Sessions.Models;

namespace Services.Sessions.Interface;

/// <summary>
/// In-process (single-replica) registry of currently-open hub connections, keyed by SignalR
/// ConnectionId, so a revocation can actually abort an IDLE connection - not merely send it a
/// best-effort notice it might never process (<see cref="ISessionRevocationNotifier"/> alone cannot do
/// this: a client that stops invoking anything, or that ignores the message, keeps its socket open and
/// keeps receiving whatever the hub pushes to it). This registry is the real per-connection handle
/// needed to force that connection closed server-side, tied to the connection's own lifetime
/// (<see cref="Services.Hubs.BaseHub.OnConnectedAsync"/>/<see cref="Services.Hubs.BaseHub.OnDisconnectedAsync"/>
/// register/unregister it directly, so it can never outlive the connection it describes).
///
/// Scope, honestly stated: this registry only knows about connections on THIS replica process. A
/// revocation's same-replica connections are aborted immediately (<see cref="AbortSession"/>, called
/// from <see cref="Service.SessionRevocationNotifier"/> right after its best-effort group send).
/// Connections parked on a DIFFERENT replica are closed within a bounded delay by
/// <see cref="Service.SessionMaintenanceHostedService"/>'s periodic revalidation sweep, which re-checks
/// every locally-registered connection against the shared PostgreSQL database (the one piece of
/// infrastructure every replica already shares) - not by this registry reaching across replicas, which
/// it cannot do without a backplane (plan 21, explicitly out of scope).
/// </summary>
public interface ISessionConnectionRegistry
{
	void Register(LiveSessionConnection connection);

	/// <summary>Safe to call unconditionally on every disconnect, even one never registered (e.g. a connection whose OnConnectedAsync bailed out before reaching the registration point) - a no-op if the key is absent.</summary>
	void Unregister(string connectionId);

	/// <summary>Aborts every connection currently registered for this SessionId on THIS replica. Returns the number of connections aborted (0 if none are registered here - which is the normal case on a replica that never held this session's connection). A single connection's Abort() throwing is caught and logged internally - it never skips any other connection in this same call.</summary>
	int AbortSession(string sessionId);

	/// <summary>A point-in-time copy of every connection registered on this replica, for <see cref="Service.SessionMaintenanceHostedService"/>'s periodic bulk revalidation. Safe to enumerate without holding any lock on the live registry.</summary>
	IReadOnlyCollection<LiveSessionConnection> Snapshot();
}
