namespace Database.Entities.Sync;

/// <summary>
/// The server-authorized author-key registry (stage 11): the public half of one author context, keyed
/// by the complete wire <c>AuthorKeyReference</c> (account, chat, key id, key version). This is the ONLY
/// place an author's verification key may come from - docs/sync-contract-v1.md: "The key comes from the
/// server-authorized author-key registry over authenticated TLS, never from an untrusted JWS header or
/// embedded client-supplied key."
///
/// The binding is one-way: once a reference holds an <see cref="Spki"/>, a different one for the same
/// reference is a conflict and is refused ("Once a reference is bound to SPKI, a different SPKI for that
/// same reference is a conflict; rotation uses a new version") - rotation inserts a new row with a new
/// <see cref="AuthorKeyVersion"/>, which the primary key makes a different row by construction. Old
/// versions are retained so messages signed under them stay verifiable forever.
///
/// The row is written by a currently trusted device of <see cref="AccountId"/>; the account is taken
/// from the authenticated session, never from the payload, so a device can only ever publish its own
/// account's author key. <see cref="RegisteredByDeviceRegistrationId"/> is audit only - a recovered or
/// approved device legitimately re-registers the SAME key material for the same reference (that is what
/// preserves authorship identity across devices), which is an idempotent no-op rather than a conflict.
/// </summary>
public class SyncAuthorKeyEntity
{
	public SyncAuthorKeyEntity(
		string accountId, string chatId, string authorKeyId, string authorKeyVersion, byte[] spki,
		string registeredByDeviceRegistrationId, DateTime createdAtUtc)
	{
		AccountId = accountId;
		ChatId = chatId;
		AuthorKeyId = authorKeyId;
		AuthorKeyVersion = authorKeyVersion;
		Spki = spki;
		RegisteredByDeviceRegistrationId = registeredByDeviceRegistrationId;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncAuthorKeyEntity() { }

	public string AccountId { get; set; } = null!;

	public string ChatId { get; set; } = null!;

	public string AuthorKeyId { get; set; } = null!;

	/// <summary>Canonical decimal Int64 string, like every other version on the wire.</summary>
	public string AuthorKeyVersion { get; set; } = null!;

	/// <summary>DER SubjectPublicKeyInfo for P-256. Public material: it is what recipients import to verify the saved signed bytes.</summary>
	public byte[] Spki { get; set; } = null!;

	/// <summary>Audit only - which trusted registration first published this key. Never an authorization input.</summary>
	public string RegisteredByDeviceRegistrationId { get; set; } = null!;

	public DateTime CreatedAtUtc { get; set; }

	public virtual SyncAccountEntity Account { get; set; } = null!;
	public virtual SyncChatEntity Chat { get; set; } = null!;
	public virtual SyncDeviceRegistrationEntity RegisteredByDeviceRegistration { get; set; } = null!;
}
