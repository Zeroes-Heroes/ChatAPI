namespace Database.Entities.Sync;

/// <summary>
/// One client-sealed recovery envelope per (account context, recovery version). The plaintext is the
/// account's authorship material plus the recovery key; it is sealed on-device under the Argon2id-derived
/// key, so the server can never open it. Wrapped a second time at rest under the mounted master secret
/// (<see cref="Services.Keys.Interface.IMessageKeyProtector"/>, purpose "recovery-envelope") so a DB dump
/// alone cannot even start an offline guess. KDF metadata is not secret and is authenticated by the
/// client's own AAD, so tampering with it fails the open instead of deriving a different key.
/// </summary>
public class SyncRecoveryEnvelopeEntity
{
	public SyncRecoveryEnvelopeEntity(
		string accountId, long recoveryVersion, string formatVersion, string kdfAlgorithm, int kdfVersion,
		byte[] salt, int memoryKiB, int iterations, int parallelism,
		string masterKeyVersion, byte[] nonce, byte[] protectedEnvelope, byte[] tag,
		byte[] verificationSpki, string createdByDeviceRegistrationId, DateTime createdAtUtc)
	{
		AccountId = accountId;
		RecoveryVersion = recoveryVersion;
		FormatVersion = formatVersion;
		KdfAlgorithm = kdfAlgorithm;
		KdfVersion = kdfVersion;
		Salt = salt;
		MemoryKiB = memoryKiB;
		Iterations = iterations;
		Parallelism = parallelism;
		MasterKeyVersion = masterKeyVersion;
		Nonce = nonce;
		ProtectedEnvelope = protectedEnvelope;
		Tag = tag;
		VerificationSpki = verificationSpki;
		CreatedByDeviceRegistrationId = createdByDeviceRegistrationId;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncRecoveryEnvelopeEntity() { }

	public string AccountId { get; set; } = null!;

	/// <summary>Monotonic per account context, part of the client's AAD. Rows are append-only: no version is ever removed, because any of them may be the secret the user's password manager holds.</summary>
	public long RecoveryVersion { get; set; }

	/// <summary>This row's own storage-shape version, independent of the master key version - same role as <see cref="SyncMessageKeyEntity.FormatVersion"/>.</summary>
	public string FormatVersion { get; set; } = null!;

	/// <summary>Always "argon2id" for v1; stored (not assumed) because it is one of the AAD's own fields.</summary>
	public string KdfAlgorithm { get; set; } = null!;

	/// <summary>Argon2 version as the decimal integer the AAD encodes ("19" = 0x13).</summary>
	public int KdfVersion { get; set; }

	/// <summary>Exactly 16 random bytes, generated on the device.</summary>
	public byte[] Salt { get; set; } = null!;

	public int MemoryKiB { get; set; }

	public int Iterations { get; set; }

	public int Parallelism { get; set; }

	/// <summary>Which mounted master key version wrapped <see cref="ProtectedEnvelope"/>; bound into the wrap AAD, so a row can only ever be unwrapped under the version it says it used.</summary>
	public string MasterKeyVersion { get; set; } = null!;

	/// <summary>The server wrap's own fresh 12-byte nonce (not the client envelope's).</summary>
	public byte[] Nonce { get; set; } = null!;

	/// <summary>The complete client envelope (its own nonce + ciphertext + tag, canonically concatenated by <see cref="Services.Recovery.Service.RecoveryEnvelopeService"/>), encrypted under the master key.</summary>
	public byte[] ProtectedEnvelope { get; set; } = null!;

	/// <summary>The server wrap's own 16-byte tag.</summary>
	public byte[] Tag { get; set; } = null!;

	/// <summary>
	/// DER SubjectPublicKeyInfo of the recovery key whose private half lives INSIDE this envelope. Public
	/// material, stored in clear: it is what lets the server verify a recovering device's proof of secret
	/// knowledge (chat-recovery-proof-v1) without ever holding the secret or the private key.
	/// </summary>
	public byte[] VerificationSpki { get; set; } = null!;

	/// <summary>Audit only - which trusted device uploaded this envelope; never authority for serving it.</summary>
	public string CreatedByDeviceRegistrationId { get; set; } = null!;

	public DateTime CreatedAtUtc { get; set; }

	/// <summary>
	/// Set once the device confirmed the system password manager actually stored this version's secret,
	/// which closes setup for this account context. EVERY stored version stays usable for recovery
	/// regardless: a save can succeed while the confirmation is lost, so the server cannot tell which
	/// attempt the password manager kept.
	/// </summary>
	public DateTime? ConfirmedAtUtc { get; set; }

	public virtual SyncAccountEntity Account { get; set; } = null!;
}
