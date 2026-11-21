using Services.Keys.Models;

namespace Services.Keys.Interface;

/// <summary>
/// AES-256-GCM protection of a raw key under the server master secret, with associated data that
/// binds every non-ciphertext identifier in <see cref="KeyProtectionContext"/> plus the master key
/// version actually used - see docs/sync-contract-v1.md: "AESGCM protected envelope binds ALL
/// nonciphertext identifiers via explicit versioned unambiguous AAD encoding." Pure cryptography:
/// no PostgreSQL access, no entity types - see <see cref="IMessageKeyProvisioningService"/> for the
/// piece that combines this with storage.
/// </summary>
public interface IMessageKeyProtector
{
	/// <summary>Protects <paramref name="rawKey"/> under <see cref="IMasterSecretProvider.CurrentVersion"/>.</summary>
	ProtectedKeyMaterial Protect(ReadOnlySpan<byte> rawKey, KeyProtectionContext context);

	/// <exception cref="Services.Keys.MasterSecretUnavailableException"><paramref name="material"/>'s master version is not available.</exception>
	/// <exception cref="Services.Keys.KeyProtectionIntegrityException">Authentication failed - tampered bytes or a mismatched <paramref name="context"/>.</exception>
	byte[] Unprotect(ProtectedKeyMaterial material, KeyProtectionContext context);
}
