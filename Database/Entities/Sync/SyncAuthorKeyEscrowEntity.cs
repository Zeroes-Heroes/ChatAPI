namespace Database.Entities.Sync;

/// <summary>
/// One author key sealed to a recovery version's verification key. The recovery envelope is a one-time
/// snapshot (its plaintext can only be re-sealed by someone holding the secret), so authorship created
/// AFTER setup would otherwise never be recoverable. A device needs no secret to add to this set: it
/// seals to the public <see cref="SyncRecoveryEnvelopeEntity.VerificationSpki"/> of that version, whose
/// private half lives inside the envelope. Append-only and idempotent per (account, version, author key).
/// Protected at rest under the mounted master secret, exactly like the envelope itself.
/// </summary>
public class SyncAuthorKeyEscrowEntity
{
	public SyncAuthorKeyEscrowEntity(
		string accountId, long recoveryVersion, string authorKeyId, string chatId, string authorKeyVersion,
		string formatVersion, byte[] ephemeralSpki, string masterKeyVersion, byte[] nonce,
		byte[] protectedPayload, byte[] tag, string createdByDeviceRegistrationId, DateTime createdAtUtc)
	{
		AccountId = accountId;
		RecoveryVersion = recoveryVersion;
		AuthorKeyId = authorKeyId;
		ChatId = chatId;
		AuthorKeyVersion = authorKeyVersion;
		FormatVersion = formatVersion;
		EphemeralSpki = ephemeralSpki;
		MasterKeyVersion = masterKeyVersion;
		Nonce = nonce;
		ProtectedPayload = protectedPayload;
		Tag = tag;
		CreatedByDeviceRegistrationId = createdByDeviceRegistrationId;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncAuthorKeyEscrowEntity() { }

	public string AccountId { get; set; } = null!;

	/// <summary>Which recovery version's verification key this entry was sealed to; part of the client's AAD.</summary>
	public long RecoveryVersion { get; set; }

	public string AuthorKeyId { get; set; } = null!;

	public string ChatId { get; set; } = null!;

	public string AuthorKeyVersion { get; set; } = null!;

	public string FormatVersion { get; set; } = null!;

	/// <summary>DER SubjectPublicKeyInfo of the sender's ephemeral ECDH key. Public, in clear, and inside the client's AAD.</summary>
	public byte[] EphemeralSpki { get; set; } = null!;

	public string MasterKeyVersion { get; set; } = null!;

	public byte[] Nonce { get; set; } = null!;

	/// <summary>The client ciphertext (its own nonce + ciphertext + tag), wrapped under the master key.</summary>
	public byte[] ProtectedPayload { get; set; } = null!;

	public byte[] Tag { get; set; } = null!;

	/// <summary>Audit only - which trusted device contributed this entry.</summary>
	public string CreatedByDeviceRegistrationId { get; set; } = null!;

	public DateTime CreatedAtUtc { get; set; }

	public virtual SyncAccountEntity Account { get; set; } = null!;
}
