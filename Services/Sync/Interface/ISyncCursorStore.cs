using Database.Entities.Sync;
using Database.Enums.Sync;

namespace Services.Sync.Interface;

/// <summary>
/// Mints and resolves the opaque page cursors of one sync session (docs/sync-contract-v1.md: "Cursors
/// are opaque random 256-bit tokens referencing durable PostgreSQL state shared by replicas"). The
/// token is random and carries no meaning; everything a page needs is the row this store keeps, which
/// is why a page taken on another API replica continues exactly where the previous one stopped.
///
/// A cursor is NOT authority: <see cref="ResolveAsync"/> only says "this token names this durable
/// position, bound to this session/device/rights/snapshot". The caller still revalidates the session
/// and the current rights on every page.
/// </summary>
public interface ISyncCursorStore
{
	/// <summary>
	/// A new cursor row, staged on the caller's own <see cref="Database.Context.AppDbContext"/> and
	/// committed by the caller - so a cursor can never exist for a page that was not actually answered.
	/// Its <see cref="SyncCursorEntity.Id"/> is the token to hand out.
	/// </summary>
	SyncCursorEntity Mint(
		SyncSessionEntity session, SyncCursorSurface surface, SyncStreamKind streamKind,
		string? chatId, string? membershipId, long fromSequence, long upperBoundSequence, DateTime nowUtc);

	/// <summary>The row this token names, or null for an unknown, expired or malformed token - which the caller answers as CursorInvalid.</summary>
	Task<SyncCursorEntity?> ResolveAsync(string? token, DateTime nowUtc, CancellationToken ct = default);
}
