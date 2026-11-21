using System.Text.Json;
using System.Text.Json.Serialization;
using Services.Crypto;
using Services.Crypto.Service;
using Services.Sync.Contracts;
using Xunit;

namespace CryptoTests;

/// <summary>
/// Verifies a real package produced by the actual TypeScript client crypto module (see
/// Fixtures/README.md) with the actual .NET <see cref="MessagePackageVerifier"/> - genuine
/// cross-language interoperability, not two independent implementations tested in isolation.
/// </summary>
public class CrossLanguageFixtureTests
{
	private sealed record Fixture(
		string AuthorPublicKeySpkiBase64Url,
		MessageBinding ExpectedBinding,
		string Plaintext,
		string MessageKeyBase64Url,
		string SignedPackage,
		string PackageDigest,
		TamperedVariants Tampered);

	private sealed record TamperedVariants(string BitFlippedSignature, string SwappedBindingField, string MalformedHeaderAlg);

	private static Fixture Load()
	{
		string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cross-language-fixture.json");
		string json = File.ReadAllText(path);
		var options = new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			Converters = { new JsonStringEnumConverter() },
		};
		return JsonSerializer.Deserialize<Fixture>(json, options) ?? throw new InvalidOperationException("Fixture deserialized to null.");
	}

	[Fact]
	public void RealClientPackage_VerifiesWithTheRealDotNetVerifier()
	{
		Fixture fixture = Load();
		Assert.True(Base64Url.TryDecode(fixture.AuthorPublicKeySpkiBase64Url, out byte[] spki));

		var verifier = new MessagePackageVerifier();
		var verified = verifier.Verify(fixture.SignedPackage, fixture.ExpectedBinding, spki);

		Assert.Equal(fixture.ExpectedBinding, verified.Binding);
		Assert.Equal(fixture.PackageDigest, verifier.ComputeDigest(fixture.SignedPackage));
	}

	[Fact]
	public void RealClientPackage_ContentDecryptsWithTheRealMessageKey()
	{
		// The server itself never decrypts message content (see docs/sync-contract-v1.md), but the
		// transient MessageKey does pass through the server (SendMessageRequest.MessageKey) on its way
		// to being protected - so AES-256-GCM interop on that exact key/nonce/AAD/tag is still a real,
		// in-scope cross-language proof, done here with .NET's own AesGcm (not a duplicate of the
		// client's logic): if .NET's AesGcm cannot open what QuickCrypto's AES-GCM sealed, the
		// key-wrap step (also AesGcm) would be exercised against the wrong assumptions in production.
		Fixture fixture = Load();
		var verifier = new MessagePackageVerifier();
		Assert.True(Base64Url.TryDecode(fixture.AuthorPublicKeySpkiBase64Url, out byte[] spki));
		var verified = verifier.Verify(fixture.SignedPackage, fixture.ExpectedBinding, spki);

		Assert.True(Base64Url.TryDecode(fixture.MessageKeyBase64Url, out byte[] messageKey));
		using var aesGcm = new System.Security.Cryptography.AesGcm(messageKey, 16);
		byte[] plaintext = new byte[verified.CiphertextBytes.Length];

		string[] fields =
		[
			"chat-message-v1", "1",
			fixture.ExpectedBinding.Operation.Device.AccountContextId,
			fixture.ExpectedBinding.Operation.Device.DeviceRegistrationId,
			fixture.ExpectedBinding.Operation.Device.LocalInstanceId,
			fixture.ExpectedBinding.Operation.ClientOperationId,
			fixture.ExpectedBinding.ChatId,
			fixture.ExpectedBinding.AuthorKey.AuthorKeyId,
			fixture.ExpectedBinding.AuthorKey.AuthorKeyVersion,
			"SendText",
		];
		byte[] aad = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields));

		aesGcm.Decrypt(verified.NonceBytes, verified.CiphertextBytes, verified.TagBytes, plaintext, aad);
		Assert.Equal(fixture.Plaintext, System.Text.Encoding.UTF8.GetString(plaintext));
	}

	[Fact]
	public void TamperedVariant_BitFlippedSignature_IsRejected()
	{
		Fixture fixture = Load();
		Assert.True(Base64Url.TryDecode(fixture.AuthorPublicKeySpkiBase64Url, out byte[] spki));
		var verifier = new MessagePackageVerifier();
		Assert.Throws<JwsSignatureException>(() => verifier.Verify(fixture.Tampered.BitFlippedSignature, fixture.ExpectedBinding, spki));
	}

	[Fact]
	public void TamperedVariant_SwappedBindingField_IsRejected()
	{
		Fixture fixture = Load();
		Assert.True(Base64Url.TryDecode(fixture.AuthorPublicKeySpkiBase64Url, out byte[] spki));
		var verifier = new MessagePackageVerifier();
		Assert.Throws<JwsSignatureException>(() => verifier.Verify(fixture.Tampered.SwappedBindingField, fixture.ExpectedBinding, spki));
	}

	[Fact]
	public void TamperedVariant_MalformedHeaderAlg_IsRejected()
	{
		Fixture fixture = Load();
		Assert.True(Base64Url.TryDecode(fixture.AuthorPublicKeySpkiBase64Url, out byte[] spki));
		var verifier = new MessagePackageVerifier();
		Assert.Throws<JwsFormatException>(() => verifier.Verify(fixture.Tampered.MalformedHeaderAlg, fixture.ExpectedBinding, spki));
	}
}
