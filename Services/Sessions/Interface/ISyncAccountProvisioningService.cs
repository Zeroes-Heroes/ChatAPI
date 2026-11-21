using Database.Context;
using Database.Entities.Sync;

namespace Services.Sessions.Interface;

/// <summary>
/// Bridges the legacy <c>UserEntity</c> (bootstrap SMS/PIN identity) into the sync protocol's
/// AccountContextId - stage 02's <see cref="SyncAccountEntity"/> doc comment defines the shape but
/// stage 02 deliberately created no service to populate it. This is that first write path, scoped
/// narrowly to "find or create the one active context for this legacy user" - it does not touch
/// <see cref="SyncAccountEntity.EndedAtUtc"/> or create a fresh context for an existing legacy user
/// (that lifecycle transition belongs to plan/06-recovery.md's "new start" flow).
/// </summary>
public interface ISyncAccountProvisioningService
{
	/// <summary>Returns the existing active (EndedAtUtc IS NULL) context for <paramref name="legacyUserId"/>, or atomically creates one if none exists yet. Safe under concurrent first calls for the same user (relies on <see cref="Database.Configurations.Sync.SyncAccountConfiguration"/>'s filtered unique index).</summary>
	Task<SyncAccountEntity> GetOrCreateActiveAccountAsync(AppDbContext db, int legacyUserId, DateTime now, CancellationToken ct = default);
}
