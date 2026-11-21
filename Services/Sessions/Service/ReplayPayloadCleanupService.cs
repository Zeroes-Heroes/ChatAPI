using Microsoft.EntityFrameworkCore;
using Database.Context;
using Services.Sessions.Interface;

namespace Services.Sessions.Service;

/// <summary>See <see cref="IReplayPayloadCleanupService"/>.</summary>
public sealed class ReplayPayloadCleanupService : IReplayPayloadCleanupService
{
	public Task<int> PurgeExpiredAsync(AppDbContext db, DateTime now, int batchSize, CancellationToken ct = default) =>
		// `ctid` (Postgres's own physical row identifier) bounds the UPDATE to at most `batchSize` rows
		// via a sub-select, since Postgres's UPDATE has no direct LIMIT clause - the composite primary
		// key (SessionId, RefreshOperationId) would work equally well as the sub-select's key, this is
		// just the simplest correct form. Each call purges one bounded batch; a caller that wants to
		// drain everything currently eligible loops until this returns 0 (see the interface doc comment).
		db.Database.ExecuteSqlInterpolatedAsync(
			$"""
			UPDATE "SyncRefreshTokenRotations" t
			SET "ProtectedReplayPayload" = NULL, "ProtectedReplayNonce" = NULL, "ProtectedReplayTag" = NULL, "ProtectedReplayMasterKeyVersion" = NULL
			WHERE t.ctid IN (
				SELECT ctid FROM "SyncRefreshTokenRotations"
				WHERE "ReplayExpiresAtUtc" < {now} AND "ProtectedReplayPayload" IS NOT NULL
				LIMIT {batchSize}
			)
			""", ct);
}
