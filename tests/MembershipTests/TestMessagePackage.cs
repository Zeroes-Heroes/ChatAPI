using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Services.Crypto;
using Services.Sync.Contracts;

namespace MembershipTests;

/// <summary>
/// Builds real SendText packages the way the CLIENT does - .NET's own AesGcm/ECDsa directly, never
/// through the production verifier that is under test - so these tests exercise the whole acceptance
/// path against genuine ciphertext and genuine ES256 signatures, not a stub.
/// </summary>
internal sealed class TestAuthorKey : IDisposable
{
	private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

	public byte[] Spki => _key.ExportSubjectPublicKeyInfo();

	public void Dispose() => _key.Dispose();

	/// <summary>
	/// One immutable package plus the transient 32-byte content key the request carries separately. A
	/// retry must resend exactly this pair, never a rebuilt one.
	/// </summary>
	public (string SignedPackage, byte[] MessageKey) Build(MessageBinding binding, string plaintext)
	{
		byte[] messageKey = RandomNumberGenerator.GetBytes(32);
		byte[] nonce = RandomNumberGenerator.GetBytes(12);
		byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
		byte[] ciphertext = new byte[plaintextBytes.Length];
		byte[] tag = new byte[16];

		using (AesGcm aes = new(messageKey, 16))
		{
			aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, Aad(binding));
		}

		EncryptedContent content = new(Base64Url.Encode(nonce), Base64Url.Encode(ciphertext), Base64Url.Encode(tag));
		byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
			new SignedMessagePayload(binding, content), SyncProtocol.CreateJsonOptions());

		string header = Base64Url.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
			new Dictionary<string, string> { ["alg"] = "ES256", ["typ"] = "chat-message+jws", ["kid"] = binding.AuthorKey.AuthorKeyId })));
		string signingInput = $"{header}.{Base64Url.Encode(payload)}";
		byte[] signature = _key.SignData(
			Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

		return ($"{signingInput}.{Base64Url.Encode(signature)}", messageKey);
	}

	/// <summary>docs/sync-contract-v1.md's exact AAD array - the one the production verifier's own decrypt side must agree with.</summary>
	private static byte[] Aad(MessageBinding binding) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]
	{
		"chat-message-v1",
		"1",
		binding.Operation.Device.AccountContextId,
		binding.Operation.Device.DeviceRegistrationId,
		binding.Operation.Device.LocalInstanceId,
		binding.Operation.ClientOperationId,
		binding.ChatId,
		binding.AuthorKey.AuthorKeyId,
		binding.AuthorKey.AuthorKeyVersion,
		"SendText",
	}));
}
