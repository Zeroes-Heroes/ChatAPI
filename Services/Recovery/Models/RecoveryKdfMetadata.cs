using Services.Crypto;
using Services.Sync.Contracts;

namespace Services.Recovery.Models;

/// <summary>
/// Validated Argon2id metadata for one recovery envelope - the only shape the rest of this module
/// accepts, so "was this validated?" is answered by the type rather than by remembering to call a
/// checker. Per docs/sync-contract-v1.md: "RecoveryKdf algorithm is argon2id v0x13 with 32-byte output,
/// 16-byte random salt ... Bound resource parameters before derivation. ... The KDF metadata is
/// authenticated as well as validated before derivation."
/// </summary>
public sealed record RecoveryKdfMetadata(string Algorithm, int Version, byte[] Salt, int MemoryKiB, int Iterations, int Parallelism)
{
	public const string Argon2id = "argon2id";

	/// <summary>
	/// Validates a client-supplied <see cref="RecoveryKdf"/> against the pinned algorithm/salt/bounds.
	/// Bounds come from <see cref="SyncProtocol"/>, the same constants the client checks before it
	/// derives - not a second, server-only opinion about what is acceptable.
	/// </summary>
	/// <exception cref="RecoveryEnvelopeInvalidException">Any field is absent, non-canonical or out of bounds.</exception>
	public static RecoveryKdfMetadata FromWire(RecoveryKdf kdf)
	{
		if (!string.Equals(kdf.Algorithm, Argon2id, StringComparison.Ordinal))
		{
			throw new RecoveryEnvelopeInvalidException("KDF algorithm must be exactly \"argon2id\".");
		}
		if (!Base64Url.TryDecode(kdf.Salt, out byte[] salt) || salt.Length != SyncProtocol.RecoveryKdfSaltBytes)
		{
			throw new RecoveryEnvelopeInvalidException($"KDF salt must be canonical unpadded base64url of exactly {SyncProtocol.RecoveryKdfSaltBytes} bytes.");
		}
		if (kdf.MemoryKiB < SyncProtocol.RecoveryKdfMinMemoryKiB || kdf.MemoryKiB > SyncProtocol.RecoveryKdfMaxMemoryKiB)
		{
			throw new RecoveryEnvelopeInvalidException("KDF memory is outside the permitted bounds.");
		}
		if (kdf.Iterations < SyncProtocol.RecoveryKdfMinIterations || kdf.Iterations > SyncProtocol.RecoveryKdfMaxIterations)
		{
			throw new RecoveryEnvelopeInvalidException("KDF iterations are outside the permitted bounds.");
		}
		if (kdf.Parallelism < SyncProtocol.RecoveryKdfMinParallelism || kdf.Parallelism > SyncProtocol.RecoveryKdfMaxParallelism)
		{
			throw new RecoveryEnvelopeInvalidException("KDF parallelism is outside the permitted bounds.");
		}
		// RFC 9106 §3.1's own m >= 8*p floor, kept explicit rather than assumed to follow from the
		// bounds above - it is the one constraint that couples two of the parameters.
		if (kdf.MemoryKiB < 8 * kdf.Parallelism)
		{
			throw new RecoveryEnvelopeInvalidException("KDF memory must be at least 8 KiB per lane.");
		}

		return new RecoveryKdfMetadata(Argon2id, SyncProtocol.RecoveryKdfVersion, salt, kdf.MemoryKiB, kdf.Iterations, kdf.Parallelism);
	}

	/// <summary>The wire shape to hand back to a recovering device - exactly the values the sealing device's own AAD was built from.</summary>
	public RecoveryKdf ToWire() => new(Algorithm, Base64Url.Encode(Salt), MemoryKiB, Iterations, Parallelism);
}
