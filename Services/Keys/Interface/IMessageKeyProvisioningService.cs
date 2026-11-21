using Database.Entities.Sync;
using Services.Keys.Models;

namespace Services.Keys.Interface;

/// <summary>
/// Combines <see cref="IMessageKeyProtector"/> and <see cref="IProtectedMessageKeyStore"/> behind the
/// one entry point callers actually need: protect a freshly-accepted message's content key for
/// storage, or unwrap/rewrap an existing row for an already-authorized caller. This is the module's
/// only key-export path, and it requires the caller to assert the exact (messageId, chatId) it has
/// already authorized - there is no broader "export this key" primitive.
/// </summary>
public interface IMessageKeyProvisioningService
{
	/// <summary>
	/// Builds (but does not store) the protected-key row for a new message. The caller adds the
	/// returned entity to its own <see cref="Database.Context.AppDbContext"/> (e.g. via
	/// <see cref="IProtectedMessageKeyStore.Stage"/>) alongside the message and everything else in
	/// the same transaction.
	/// </summary>
	SyncMessageKeyEntity ProtectForStorage(string messageId, string chatId, ReadOnlySpan<byte> rawMessageKey, DateTime createdAtUtc);

	/// <exception cref="Services.Keys.KeyContextMismatchException"><paramref name="expectedContext"/> does not match <paramref name="row"/>.</exception>
	/// <exception cref="Services.Keys.MasterSecretUnavailableException">The row's master key version is unavailable.</exception>
	/// <exception cref="Services.Keys.KeyProtectionIntegrityException">Authentication failed.</exception>
	byte[] UnwrapForAuthorizedDelivery(SyncMessageKeyEntity row, MessageKeyAccessContext expectedContext);

	/// <summary>
	/// Re-encrypts <paramref name="row"/> under the current master key version. Mutates and returns
	/// the same entity (new Nonce/ProtectedKey/Tag/KeyVersion); the caller still owns
	/// <c>SaveChangesAsync</c>. Never changes <see cref="SyncMessageKeyEntity.MessageId"/>,
	/// <see cref="SyncMessageKeyEntity.ChatId"/> or the underlying raw key - per docs/sync-contract-v1.md:
	/// "Rotation never changes message ciphertext, author package, MessageId or rights version."
	/// </summary>
	SyncMessageKeyEntity Rewrap(SyncMessageKeyEntity row, MessageKeyAccessContext expectedContext);
}
