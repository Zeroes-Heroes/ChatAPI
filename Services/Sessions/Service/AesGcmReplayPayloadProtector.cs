using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Services.Keys;
using Services.Keys.Interface;
using Services.Sessions.Interface;
using Services.Sessions.Models;

namespace Services.Sessions.Service;

/// <summary>AES-256-GCM key-wrap using <see cref="IMasterSecretProvider"/>, for the refresh-rotation replay cache - see <see cref="IReplayPayloadProtector"/>'s doc comment for why this is a sibling of <see cref="Services.Keys.Service.AesGcmMessageKeyProtector"/>, not a reuse of it.</summary>
public sealed class AesGcmReplayPayloadProtector(IMasterSecretProvider masterSecretProvider) : IReplayPayloadProtector
{
	private const int NonceLength = 12;
	private const int TagLength = 16;
	private const string Domain = "chat-server-refresh-replay-v1";
	private const string FormatVersion = "1";

	public ProtectedReplayPayload Protect(ReadOnlySpan<byte> plaintext, ReplayProtectionContext context)
	{
		string masterKeyVersion = masterSecretProvider.CurrentVersion;
		byte[] masterKey = masterSecretProvider.GetKey(masterKeyVersion);
		try
		{
			byte[] aad = BuildAad(masterKeyVersion, context);
			byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
			byte[] ciphertext = new byte[plaintext.Length];
			byte[] tag = new byte[TagLength];

			using var aesGcm = new AesGcm(masterKey, TagLength);
			aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);

			return new ProtectedReplayPayload(masterKeyVersion, nonce, ciphertext, tag);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(masterKey);
		}
	}

	public byte[] Unprotect(ProtectedReplayPayload payload, ReplayProtectionContext context)
	{
		byte[] masterKey = masterSecretProvider.GetKey(payload.MasterKeyVersion);
		try
		{
			byte[] aad = BuildAad(payload.MasterKeyVersion, context);

			if (payload.Nonce.Length != NonceLength)
			{
				throw new KeyProtectionIntegrityException($"Stored nonce must be exactly {NonceLength} bytes.");
			}
			if (payload.Tag.Length != TagLength)
			{
				throw new KeyProtectionIntegrityException($"Stored tag must be exactly {TagLength} bytes.");
			}

			byte[] plaintext = new byte[payload.Ciphertext.Length];
			using var aesGcm = new AesGcm(masterKey, TagLength);
			try
			{
				aesGcm.Decrypt(payload.Nonce, payload.Ciphertext, payload.Tag, plaintext, aad);
			}
			catch (CryptographicException ex)
			{
				throw new KeyProtectionIntegrityException("Protected replay payload failed authentication (tampered bytes or mismatched context).", ex);
			}

			return plaintext;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(masterKey);
		}
	}

	private static byte[] BuildAad(string masterKeyVersion, ReplayProtectionContext context)
	{
		string[] fields = [Domain, FormatVersion, masterKeyVersion, context.AccountId, context.SessionId, context.RefreshOperationId];
		return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields));
	}
}
