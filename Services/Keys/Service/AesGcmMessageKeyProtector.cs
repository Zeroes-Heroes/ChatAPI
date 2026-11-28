using System.Security.Cryptography;
using Services.Keys.Interface;
using Services.Keys.Models;

namespace Services.Keys.Service;

/// <summary>AES-256-GCM key-wrap using <see cref="IMasterSecretProvider"/>, per docs/sync-contract-v1.md: "Server protected-key records use AES-256-GCM with 32-byte master key, fresh 12-byte nonce and 16-byte tag."</summary>
public sealed class AesGcmMessageKeyProtector(IMasterSecretProvider masterSecretProvider) : IMessageKeyProtector
{
	private const int NonceLength = 12;
	private const int TagLength = 16;

	public ProtectedKeyMaterial Protect(ReadOnlySpan<byte> rawKey, KeyProtectionContext context)
	{
		string masterKeyVersion = masterSecretProvider.CurrentVersion;
		byte[] masterKey = masterSecretProvider.GetKey(masterKeyVersion);
		try
		{
			byte[] aad = KeyProtectionAad.Build(masterKeyVersion, context);

			byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
			byte[] ciphertext = new byte[rawKey.Length];
			byte[] tag = new byte[TagLength];

			using var aesGcm = new AesGcm(masterKey, TagLength);
			aesGcm.Encrypt(nonce, rawKey, ciphertext, tag, aad);

			return new ProtectedKeyMaterial(masterKeyVersion, nonce, ciphertext, tag);
		}
		finally
		{
			// `masterKey` is this call's own defensive copy (see IMasterSecretProvider.GetKey) - safe
			// to zero unconditionally, never shared with the provider's cache or any other caller.
			CryptographicOperations.ZeroMemory(masterKey);
		}
	}

	public byte[] Unprotect(ProtectedKeyMaterial material, KeyProtectionContext context)
	{
		byte[] masterKey = masterSecretProvider.GetKey(material.MasterKeyVersion);
		try
		{
			byte[] aad = KeyProtectionAad.Build(material.MasterKeyVersion, context);

			if (material.Nonce.Length != NonceLength)
			{
				throw new KeyProtectionIntegrityException($"Stored nonce must be exactly {NonceLength} bytes.");
			}
			if (material.Tag.Length != TagLength)
			{
				throw new KeyProtectionIntegrityException($"Stored tag must be exactly {TagLength} bytes.");
			}

			byte[] rawKey = new byte[material.Ciphertext.Length];
			using var aesGcm = new AesGcm(masterKey, TagLength);
			try
			{
				aesGcm.Decrypt(material.Nonce, material.Ciphertext, material.Tag, rawKey, aad);
			}
			catch (CryptographicException ex)
			{
				throw new KeyProtectionIntegrityException("Protected key record failed authentication (tampered bytes or mismatched context).", ex);
			}

			return rawKey;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(masterKey);
		}
	}
}
