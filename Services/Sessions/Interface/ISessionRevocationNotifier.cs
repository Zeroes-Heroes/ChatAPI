using WireContracts = Services.Sync.Contracts;

namespace Services.Sessions.Interface;

/// <summary>
/// Live revocation enforcement for an already-connected session, per docs/sync-contract-v1.md's
/// SessionControlEvent: forcibly aborts every same-replica connection for one or more sessions right now
/// (<see cref="ISessionConnectionRegistry"/>), ALWAYS strictly before attempting any best-effort courtesy
/// notice for those same sessions - a notification alone is not enforcement, since an idle or
/// non-cooperating client would otherwise keep its socket open and keep receiving pushed data, and a
/// courtesy send that merely hangs (not throws) must never delay, let alone gate, the real abort. A
/// courtesy notice is genuinely best-effort: it can be lost entirely (timeout, transport failure, or the
/// connection having already been aborted a moment earlier by this same enforcement) - a client that never
/// receives it is expected to discover the revocation the next time it attempts any authenticated action
/// (HTTP or hub) and receives a terminal rejection from <see cref="ISessionTrustChecker"/>, never a reason
/// this enforcement step itself waits or retries. A connection on a different replica is closed within
/// <see cref="Service.SessionMaintenanceHostedService"/>'s bounded polling interval instead (see its own
/// doc comment for the exact guarantee); the shared guard (<see cref="ISessionTrustChecker"/>, enforced
/// by the HTTP authorization policy and the hub filter on every subsequent operation) remains
/// authoritative regardless of any delivery/timing gap in either mechanism (e.g. a process restart
/// between commit and send). This stage targets exactly the revoked session's own connections, never
/// every connection for the account/user, per the plan's "connections revoked per device not user-wide"
/// requirement.
/// </summary>
public interface ISessionRevocationNotifier
{
	/// <summary>Single-session convenience - equivalent to <see cref="NotifyAllSessionsRevokedAsync"/> for exactly one session.</summary>
	Task NotifySessionRevokedAsync(string sessionId, WireContracts.SessionControlReason reason, CancellationToken ct = default);

	/// <summary>
	/// For a revoke that ends MULTIPLE sessions at once (one device can have several live sessions):
	/// aborts every given session's local connections FIRST, synchronously, in one pass - a per-connection
	/// Abort() failure never skips any other session or connection (see <see cref="ISessionConnectionRegistry.AbortSession"/>'s
	/// own doc comment) - THEN sends every session's courtesy notice CONCURRENTLY (never one session's full
	/// courtesy-then-next-session cycle sequentially), each individually bounded by its own internally-owned
	/// wall-clock deadline (not a <see cref="CancellationToken"/> handed to the transport and trusted to be
	/// honored promptly - the same "own the bound, don't merely hope the callee cooperates" shape
	/// <see cref="Service.SessionMaintenanceHostedService"/> uses for its own DB calls). A session's own
	/// notice failing, timing out, or the caller's own <paramref name="ct"/> already being canceled never
	/// skips enforcement for, or delays past that bound, any OTHER session in this same batch - this method
	/// itself never throws for a courtesy-send failure.
	/// </summary>
	Task NotifyAllSessionsRevokedAsync(IReadOnlyList<string> sessionIds, WireContracts.SessionControlReason reason, CancellationToken ct = default);
}
