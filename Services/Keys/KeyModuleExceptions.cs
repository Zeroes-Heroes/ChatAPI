namespace Services.Keys;

/// <summary>
/// The master secret is misconfigured, missing or unreadable (startup config error, unmounted
/// secret volume, missing version file, wrong length, ...). Never includes key bytes in its
/// message - see docs/sync-contract-v1.md: "diagnostics exclude plaintext keys and tokens."
/// </summary>
public sealed class MasterSecretUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// AES-256-GCM authentication failed while unprotecting a stored key record: tampered ciphertext/
/// nonce/tag, or an associated-data mismatch (wrong context/purpose/owner/key id/master version
/// bound in by <see cref="Services.Keys.Interface.IMessageKeyProtector"/>). Never includes key
/// material in its message.
/// </summary>
public sealed class KeyProtectionIntegrityException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Thrown by <see cref="Services.Keys.Interface.IMessageKeyProvisioningService"/> when the caller's
/// asserted (messageId, chatId) does not match the stored row - see docs/sync-contract-v1.md:
/// "Verification must bind caller's expected author/chat/message context, not self-declared package
/// claims." This is what keeps unwrap from being a generic "give me the key for this id" endpoint:
/// the caller must already know (from its own authorized state) which chat the message belongs to.
/// </summary>
public sealed class KeyContextMismatchException(string message) : Exception(message);
