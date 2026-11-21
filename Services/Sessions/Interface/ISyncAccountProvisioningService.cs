using Database.Context;
using Database.Entities.Sync;

namespace Services.Sessions.Interface;

/// <summary>
/// Bridges the legacy <c>UserEntity</c> (bootstrap SMS/PIN identity) into the sync protocol's
/// AccountContextId - stage 02's <see cref="SyncAccountEntity"/> doc comment defines the shape but
/// stage 02 deliberately created no service to populate it. This is that first write path, scoped
/// narrowly to the account-context lifecycle: find or create the one active context for this legacy
/// user, and - stage 06 - start a separate one when the old context's keys are irrecoverable.
/// </summary>
public interface ISyncAccountProvisioningService
{
	/// <summary>Returns the existing active (EndedAtUtc IS NULL) context for <paramref name="legacyUserId"/>, or atomically creates one if none exists yet. Safe under concurrent first calls for the same user (relies on <see cref="Database.Configurations.Sync.SyncAccountConfiguration"/>'s filtered unique index).</summary>
	Task<SyncAccountEntity> GetOrCreateActiveAccountAsync(AppDbContext db, int legacyUserId, DateTime now, CancellationToken ct = default);

	/// <summary>
	/// plan/06-recovery.md: "При невъзстановими ключове ново начало със същия номер създава отделен
	/// контекст." Ends the current context (sets <see cref="SyncAccountEntity.EndedAtUtc"/>) and creates
	/// a brand new AccountContextId for the same verified phone, in one transaction under the same
	/// account row lock every other trust mutation uses. The old row and everything hung off it -
	/// devices, memberships, messages, its recovery envelope, its sticky revocation barrier - is
	/// RETAINED and left bound to the old context: nothing is rebound, so this is a separate beginning,
	/// never a way to reacquire old authorship or history.
	///
	/// Idempotent per <paramref name="freshStartOperationId"/> - the device's own durable operation id,
	/// written before the request is ever sent (Services/Recovery/PendingFreshStart.ts). A retry after a
	/// lost response or a process death resolves to the context that operation already created, under the
	/// same account lock, instead of ending the successor and chaining another; a genuinely new, later
	/// user-requested fresh start carries a new operation id and so really does start over.
	/// </summary>
	/// <exception cref="SessionNotTrustedException">The legacy user has no active context to supersede (nothing to start over from), or this operation's own context has since been superseded - which is never resurrected, so the caller must start a new operation.</exception>
	Task<SyncAccountEntity> StartNewAccountContextAsync(AppDbContext db, int legacyUserId, string freshStartOperationId, DateTime now, CancellationToken ct = default);
}
