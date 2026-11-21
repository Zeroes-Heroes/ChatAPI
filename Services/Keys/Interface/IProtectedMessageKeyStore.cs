using Database.Context;
using Database.Entities.Sync;

namespace Services.Keys.Interface;

/// <summary>
/// The storage seam for protected message-key rows. Deliberately thin: it stages/reads an entity on
/// a caller-supplied <see cref="AppDbContext"/> and never calls <c>SaveChangesAsync</c> or opens a
/// transaction itself - per the plan, "Protected key rows share SAME PostgreSQL transaction with
/// caller data ... (no separate hidden SaveChanges/transaction/commit)". The caller (e.g. a future
/// message-acceptance workflow) adds the message, this row and everything else to one DbContext and
/// commits them together. Existing as its own interface - rather than callers touching
/// <c>db.SyncMessageKeys</c> directly - is what lets a future stage swap the storage backend (per
/// docs/sync-contract-v1.md's "the module allows future export") without touching protection logic.
/// </summary>
public interface IProtectedMessageKeyStore
{
	void Stage(AppDbContext db, SyncMessageKeyEntity entity);

	Task<SyncMessageKeyEntity?> FindAsync(AppDbContext db, string messageId, CancellationToken cancellationToken = default);

	/// <summary>
	/// The same read for one BOUNDED page of messages (stage 13's history pages), keyed by message id.
	/// It exists so a page does not have to choose between N round trips and bypassing this seam with a
	/// query of its own; the caller still supplies the ids it has already authorized, one page at a time.
	/// </summary>
	Task<IReadOnlyDictionary<string, SyncMessageKeyEntity>> FindManyAsync(
		AppDbContext db, IReadOnlyCollection<string> messageIds, CancellationToken cancellationToken = default);
}
