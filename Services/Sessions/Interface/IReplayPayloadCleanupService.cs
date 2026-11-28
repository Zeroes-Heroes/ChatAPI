using Database.Context;

namespace Services.Sessions.Interface;

/// <summary>
/// Bounded, periodic purge of <see cref="Database.Entities.Sync.SyncRefreshTokenRotationEntity.ProtectedReplayPayload"/>
/// (and its sibling nonce/tag/master-key-version columns) once <see cref="Database.Entities.Sync.SyncRefreshTokenRotationEntity.ReplayExpiresAtUtc"/>
/// has passed - per that entity's own doc comment ("purged (set to null) once ReplayExpiresAtUtc
/// passes"), which nothing previously implemented. Never touches
/// <see cref="Database.Entities.Sync.SyncRefreshTokenRotationEntity.OldRefreshTokenHash"/>/<see cref="Database.Entities.Sync.SyncRefreshTokenRotationEntity.NewRefreshTokenHash"/> -
/// <see cref="IRefreshTokenService.RotateAsync"/>'s reuse-detection scan needs those for the lifetime of
/// the session, not merely within the bounded replay window; only the AES-256-GCM-protected raw-token
/// bundle (the actual data-minimization concern - it is the one place a reconstructable raw token value
/// is ever persisted) is purged.
/// </summary>
public interface IReplayPayloadCleanupService
{
	/// <summary>
	/// Nulls out the protected-payload columns for up to <paramref name="batchSize"/> rows whose replay
	/// window has already passed and which have not already been purged - bounded per call so a single
	/// sweep (e.g. from <see cref="Service.SessionMaintenanceHostedService"/>'s periodic tick) never holds
	/// a long-running transaction against a potentially large table; callers that want to fully drain a
	/// large backlog should call this in a loop until it returns 0.
	/// </summary>
	/// <returns>The number of rows actually purged (0 once nothing more is eligible).</returns>
	Task<int> PurgeExpiredAsync(AppDbContext db, DateTime now, int batchSize, CancellationToken ct = default);
}
