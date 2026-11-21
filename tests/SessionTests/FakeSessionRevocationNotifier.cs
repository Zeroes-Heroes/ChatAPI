using Services.Sessions.Interface;
using WireContracts = Services.Sync.Contracts;

namespace SessionTests;

/// <summary>Records calls instead of touching SignalR - these tests exercise RefreshTokenService/DeviceTrustService's own DB-transactional behavior, not hub delivery.</summary>
internal sealed class FakeSessionRevocationNotifier : ISessionRevocationNotifier
{
	public readonly List<(string SessionId, WireContracts.SessionControlReason Reason)> Calls = [];

	public Task NotifySessionRevokedAsync(string sessionId, WireContracts.SessionControlReason reason, CancellationToken ct = default)
	{
		Calls.Add((sessionId, reason));
		return Task.CompletedTask;
	}

	public Task NotifyAllSessionsRevokedAsync(IReadOnlyList<string> sessionIds, WireContracts.SessionControlReason reason, CancellationToken ct = default)
	{
		foreach (string sessionId in sessionIds)
		{
			Calls.Add((sessionId, reason));
		}
		return Task.CompletedTask;
	}
}
