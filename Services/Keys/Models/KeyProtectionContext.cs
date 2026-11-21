namespace Services.Keys.Models;

/// <summary>
/// The non-ciphertext identifiers an AES-256-GCM protected key record is bound to via its
/// associated data, per docs/sync-contract-v1.md: "Store format/master-key versions, owner
/// context, purpose, key ID ...; ... binds all nonciphertext identifiers." <see cref="FormatVersion"/>
/// is this storage envelope's own shape version (independent of which master key wrapped it);
/// <see cref="OwnerContextId"/> is the authorization scope this key belongs to (for message
/// content keys, the chat id); <see cref="KeyId"/> identifies the protected row itself (for
/// message content keys, the message id). The master key *version* is supplied separately by
/// <see cref="Services.Keys.Interface.IMasterSecretProvider"/> at protect/unprotect time, not
/// stored on this context, because it is chosen by the provider (current version to protect,
/// whichever version a stored row says to unprotect) rather than being part of what identifies
/// the key.
/// </summary>
public sealed record KeyProtectionContext(string FormatVersion, string Purpose, string OwnerContextId, string KeyId);

public static class KeyProtectionPurposes
{
	public const string MessageContentKey = "message-content-key";

	/// <summary>Stage 06: the client-sealed recovery envelope, wrapped a second time under the mounted master secret so a PostgreSQL dump alone cannot even begin an offline attempt against the recovery secret. Owner context is the account context, key id is the recovery version.</summary>
	public const string RecoveryEnvelope = "recovery-envelope";

	/// <summary>Stage 06: an author key sealed to a recovery version's verification key (the envelope is a one-time snapshot; this is how authorship created afterwards stays recoverable). Owner context is the account context, key id is "version:authorKeyId".</summary>
	public const string AuthorKeyEscrow = "authorkey-escrow";
}

public static class KeyProtectionFormatVersions
{
	public const string V1 = "1";
}
