using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Services.Sessions.Interface;
using Services.Sessions.Models;

namespace Services.Sessions.Service;

/// <summary>
/// See <see cref="ISessionConnectionRegistry"/>. A singleton (one instance per replica process, for its
/// whole lifetime) - ONE authoritative <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by
/// ConnectionId, deliberately not a second ConnectionId-by-SessionId index: an earlier version of this
/// class kept both and tried to keep a per-session bucket's emptiness check and its removal from the
/// outer dictionary atomic by inspection alone - a concurrent <see cref="Register"/> for the same
/// SessionId landing between that check and the removal could re-populate the bucket a moment before it
/// was torn down anyway, silently orphaning the live connection it had just added from the index
/// <see cref="AbortSession"/> reads (the bucket is gone; the connection is still in
/// <c>_byConnectionId</c>, but <see cref="AbortSession"/> can no longer find it by SessionId). With only
/// one dictionary there is no second index to go stale, so this cannot happen by construction.
/// <see cref="AbortSession"/>'s cost is O(connections on this replica) rather than O(connections for one
/// session) as a result - an acceptable trade for a per-replica connection count and an operation
/// (revocation) that is rare, not a hot path.
/// </summary>
public sealed class SessionConnectionRegistry(ILogger<SessionConnectionRegistry>? logger = null) : ISessionConnectionRegistry
{
	private readonly ILogger<SessionConnectionRegistry> _logger = logger ?? NullLogger<SessionConnectionRegistry>.Instance;
	private readonly ConcurrentDictionary<string, LiveSessionConnection> _byConnectionId = new();

	public void Register(LiveSessionConnection connection) => _byConnectionId[connection.ConnectionId] = connection;

	public void Unregister(string connectionId) => _byConnectionId.TryRemove(connectionId, out _);

	public int AbortSession(string sessionId)
	{
		int aborted = 0;
		foreach (LiveSessionConnection connection in _byConnectionId.Values)
		{
			if (connection.SessionId == sessionId)
			{
				// One connection's own Abort() throwing (e.g. the underlying transport already tore the
				// connection down concurrently) must never stop this loop early - every other connection for
				// this session, and the caller's own ability to move on to the NEXT session in a multi-session
				// revoke batch, must still be attempted regardless.
				try
				{
					connection.Abort();
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Aborting connection {ConnectionId} for session {SessionId} failed; continuing with any remaining connections.", connection.ConnectionId, sessionId);
				}
				aborted++;
			}
		}
		return aborted;
	}

	public IReadOnlyCollection<LiveSessionConnection> Snapshot() => _byConnectionId.Values.ToArray();
}
