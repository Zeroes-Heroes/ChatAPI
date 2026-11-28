using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Services.Crypto;
using Services.Recovery;
using Services.Recovery.Models;
using Services.Sync.Contracts;
using Xunit;

namespace CryptoTests;

/// <summary>
/// The recovery half of the shared fixture (Fixtures/README.md): a real envelope sealed by the actual
/// TypeScript recovery module, opened here with .NET's own AesGcm and this repo's own
/// <see cref="RecoveryAad"/>. Per docs/sync-contract-v1.md the fixture uses a FIXED derived key - native
/// Argon2id derivation stays an on-device integration check and is deliberately not simulated here.
/// </summary>
public class CrossLanguageRecoveryFixtureTests
{
	private sealed record Fixture(RecoverySection Recovery);

	private sealed record RecoverySection(
		string AccountContextId,
		string Version,
		RecoveryKdf Kdf,
		string DerivedKeyBase64Url,
		string AadBase64Url,
		EncryptedContent Content,
		string Plaintext);

	private static RecoverySection Load()
	{
		string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cross-language-fixture.json");
		var options = new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			Converters = { new JsonStringEnumConverter() },
		};
		Fixture fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path), options)
			?? throw new InvalidOperationException("Fixture deserialized to null.");
		return fixture.Recovery;
	}

	[Fact]
	public void RecoveryAad_IsByteIdenticalAcrossLanguages()
	{
		RecoverySection recovery = Load();
		Assert.True(Base64Url.TryDecode(recovery.AadBase64Url, out byte[] clientAad));

		byte[] serverAad = RecoveryAad.Build(
			recovery.AccountContextId,
			long.Parse(recovery.Version),
			RecoveryKdfMetadata.FromWire(recovery.Kdf));

		Assert.Equal(clientAad, serverAad);
	}

	[Fact]
	public void RealClientEnvelope_OpensWithTheServersOwnAad()
	{
		RecoverySection recovery = Load();
		Assert.True(Base64Url.TryDecode(recovery.DerivedKeyBase64Url, out byte[] derivedKey));
		Assert.True(Base64Url.TryDecode(recovery.Content.Nonce, out byte[] nonce));
		Assert.True(Base64Url.TryDecode(recovery.Content.Ciphertext, out byte[] ciphertext));
		Assert.True(Base64Url.TryDecode(recovery.Content.Tag, out byte[] tag));

		byte[] aad = RecoveryAad.Build(recovery.AccountContextId, long.Parse(recovery.Version), RecoveryKdfMetadata.FromWire(recovery.Kdf));
		byte[] plaintext = new byte[ciphertext.Length];
		using AesGcm aes = new(derivedKey, 16);
		aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);

		Assert.Equal(recovery.Plaintext, Encoding.UTF8.GetString(plaintext));
	}

	[Theory]
	[InlineData("account")]
	[InlineData("version")]
	[InlineData("cost")]
	public void AChangedBinding_FailsToOpen(string field)
	{
		RecoverySection recovery = Load();
		Assert.True(Base64Url.TryDecode(recovery.DerivedKeyBase64Url, out byte[] derivedKey));
		Assert.True(Base64Url.TryDecode(recovery.Content.Nonce, out byte[] nonce));
		Assert.True(Base64Url.TryDecode(recovery.Content.Ciphertext, out byte[] ciphertext));
		Assert.True(Base64Url.TryDecode(recovery.Content.Tag, out byte[] tag));

		RecoveryKdf kdf = field == "cost" ? recovery.Kdf with { Iterations = recovery.Kdf.Iterations + 1 } : recovery.Kdf;
		string accountContextId = field == "account" ? "someone-else" : recovery.AccountContextId;
		long version = field == "version" ? long.Parse(recovery.Version) + 1 : long.Parse(recovery.Version);

		byte[] aad = RecoveryAad.Build(accountContextId, version, RecoveryKdfMetadata.FromWire(kdf));
		byte[] plaintext = new byte[ciphertext.Length];
		using AesGcm aes = new(derivedKey, 16);

		// The KDF metadata and the account/version binding are authenticated, not merely stored.
		Assert.Throws<AuthenticationTagMismatchException>(() => aes.Decrypt(nonce, ciphertext, tag, plaintext, aad));
	}
}
