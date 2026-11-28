using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Services.Hubs;
using Services.Hubs.Models;
using Services.Sessions.Interface;
using Services.Utilities.Statics;
using WireContracts = Services.Sync.Contracts;

namespace Services.Sessions.Service;

/// <summary>
/// Real server-side enforcement first - <see cref="NotifyAllSessionsRevokedAsync"/> calls
/// <see cref="ISessionConnectionRegistry.AbortSession"/> for EVERY given session, synchronously, before
/// attempting any courtesy send at all - then, only once every session's local connections are already
/// gone, sends a best-effort courtesy notice to the per-session SignalR group every connection for that
/// session joined in <see cref="BaseHub.OnConnectedAsync"/>, for a well-behaved, actively-listening client
/// to act on. The two are deliberately ordered and independent: enforcement never awaits anything (it is
/// plain synchronous local work), so no courtesy send - however slow, hung, or outright failing - can ever
/// delay or skip it; and every courtesy send is bounded by its OWN internally-owned wall-clock deadline
/// (<see cref="CourtesySendTimeout"/>, via <c>Task.WhenAny</c> against an owned <c>Task.Delay</c> - never a
/// <see cref="CancellationToken"/> handed to <c>SendAsync</c> and merely trusted to be honored promptly,
/// since an uncooperative transport may not observe cancellation at all). For a multi-session batch, every
/// session's courtesy send runs CONCURRENTLY, never one session's full attempt sequentially before the
/// next - a slow/hung notice for session 1 can never add its own delay on top of session 2's. A connection
/// parked on a DIFFERENT replica is closed within <see cref="SessionMaintenanceHostedService"/>'s bounded
/// polling interval instead (see that class's own doc comment for the exact guarantee) - see
/// <see cref="ISessionTrustChecker"/>'s own doc comment for why the shared DB-backed guard remains
/// authoritative regardless of any of these. A courtesy notice is best-effort ONLY: it may never be
/// delivered at all (the connection enforcement above may have already closed the very socket this notice
/// would have reached) - a client that misses it still gets a terminal rejection from
/// <see cref="ISessionTrustChecker"/> on its next authenticated attempt, which is this module's actual
/// authoritative signal, never this notice. <see cref="SessionControlEvent"/>'s own doc comment explains
/// why it is its own MessagePack-shaped type rather than the unpublished JSON wire contract type.
/// </summary>
public sealed class SessionRevocationNotifier(
	IHubContext<BaseHub> hubContext,
	ISessionConnectionRegistry connectionRegistry,
	ILogger<SessionRevocationNotifier> logger) : ISessionRevocationNotifier
{
	/// <summary>Upper bound on how long a single session's best-effort courtesy send may run before being abandoned - a hung/slow transport must not hold up anything else (enforcement already ran before this is even attempted; see this class's own doc comment).</summary>
	private static readonly TimeSpan CourtesySendTimeout = TimeSpan.FromSeconds(3);

	public static string GroupName(string sessionId) => string.Format(LiveEvents.SessionGroupFormat, sessionId);

	public Task NotifySessionRevokedAsync(string sessionId, WireContracts.SessionControlReason reason, CancellationToken ct = default) =>
		NotifyAllSessionsRevokedAsync([sessionId], reason, ct);

	public async Task NotifyAllSessionsRevokedAsync(IReadOnlyList<string> sessionIds, WireContracts.SessionControlReason reason, CancellationToken ct = default)
	{
		// Enforcement FIRST, synchronously, for every session in this batch - independent of the request's
		// own token, of transport cooperation, and of any OTHER session's courtesy send below. Plain
		// synchronous local work (no await), so nothing here can hang; a single session's own abort failure
		// is already caught/logged inside AbortSession itself and never skips the rest of this loop.
		foreach (string sessionId in sessionIds)
		{
			connectionRegistry.AbortSession(sessionId);
		}

		// Best-effort notices only, run CONCURRENTLY so one session's courtesy delay can never add to
		// another's - real enforcement for every session in this batch has already happened above.
		await Task.WhenAll(sessionIds.Select(sessionId => SendBestEffortNoticeAsync(sessionId, reason, ct)));
	}

	private async Task SendBestEffortNoticeAsync(string sessionId, WireContracts.SessionControlReason reason, CancellationToken ct)
	{
		Task sendTask;
		try
		{
			sendTask = hubContext.Clients.Group(GroupName(sessionId)).SendAsync(LiveEvents.SessionControl, new SessionControlEvent(sessionId, reason.ToString()), ct);
		}
		catch (Exception ex)
		{
			// SendAsync itself throwing synchronously (rare, but not guaranteed not to happen) - enforcement
			// already ran; nothing further to do for this session.
			logger.LogWarning(ex, "Best-effort session-control notice for session {SessionId} failed to start; local enforcement already ran regardless.", sessionId);
			return;
		}

		// A real, internally-owned wall-clock bound - `Task.Delay` here uses no token at all, so this race
		// is decided purely by elapsed time, never by whether the caller's own `ct` happens to still be
		// live or whether the transport happens to honor the token it was also handed above.
		Task winner = await Task.WhenAny(sendTask, Task.Delay(CourtesySendTimeout, CancellationToken.None));
		if (winner != sendTask)
		{
			logger.LogWarning("Best-effort session-control notice for session {SessionId} exceeded its {TimeoutSeconds}s bound; abandoning it (local enforcement already ran).", sessionId, CourtesySendTimeout.TotalSeconds);
			// Never awaited further and never allowed to propagate here - but its eventual outcome is still
			// observed (not silently discarded) purely for diagnostics, exactly like SessionMaintenanceHostedService's
			// own abandoned-operation fault observation.
			_ = sendTask.ContinueWith(
				t => logger.LogWarning(t.Exception, "Abandoned best-effort session-control notice for session {SessionId} eventually completed, but faulted.", sessionId),
				CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
			return;
		}

		try
		{
			await sendTask; // already completed (the winner) - rethrows here if it faulted, caught below.
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Best-effort session-control notice for session {SessionId} failed; local enforcement already ran regardless.", sessionId);
		}
	}
}
