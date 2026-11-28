using Database.Context;
using WireContracts = Services.Sync.Contracts;

namespace Services.Sessions.Interface;

/// <summary>Thin orchestration for the two [AllowAnonymous] refresh endpoints - account/device are always resolved server-side from SessionId, never trusted from the request.</summary>
public interface ISessionRefreshCoordinator
{
	/// <exception cref="SessionNotTrustedException">Unknown or already-ended session.</exception>
	Task<WireContracts.DeviceChallenge> RequestSessionBindingChallengeAsync(AppDbContext db, string sessionId, string refreshOperationId, CancellationToken ct = default);

	/// <summary>Never throws for an ordinary rejection - see <see cref="IRefreshTokenService.RotateAsync"/>.</summary>
	Task<WireContracts.RefreshSessionResult> RefreshAsync(AppDbContext db, WireContracts.RefreshSessionRequest request, CancellationToken ct = default);
}
