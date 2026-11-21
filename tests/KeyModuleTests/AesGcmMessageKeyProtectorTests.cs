using Services.Keys;
using Services.Keys.Models;
using Services.Keys.Service;
using Xunit;

namespace KeyModuleTests;

public class AesGcmMessageKeyProtectorTests
{
	private static KeyProtectionContext Context(string ownerContextId = "chat-1", string keyId = "msg-1", string purpose = KeyProtectionPurposes.MessageContentKey, string formatVersion = KeyProtectionFormatVersions.V1) =>
		new(formatVersion, purpose, ownerContextId, keyId);

	private static byte[] RandomKey()
	{
		byte[] key = new byte[32];
		System.Security.Cryptography.RandomNumberGenerator.Fill(key);
		return key;
	}

	[Fact]
	public void Protect_Unprotect_RoundTrips()
	{
		using var dir = new TestMasterSecretDirectory();
		dir.WriteVersion("v1");
		var protector = new AesGcmMessageKeyProtector(new FileMasterSecretProvider(dir.OptionsFor("v1")));

		byte[] rawKey = RandomKey();
		ProtectedKeyMaterial material = protector.Protect(rawKey, Context());
		Assert.Equal("v1", material.MasterKeyVersion);
		Assert.Equal(12, material.Nonce.Length);
		Assert.Equal(16, material.Tag.Length);

		byte[] recovered = protector.Unprotect(material, Context());
		Assert.Equal(rawKey, recovered);
	}

	[Fact]
	public void Protect_TwoCallsSameKey_ProduceDifferentNonceAndCiphertext()
	{
		using var dir = new TestMasterSecretDirectory();
		dir.WriteVersion("v1");
		var protector = new AesGcmMessageKeyProtector(new FileMasterSecretProvider(dir.OptionsFor("v1")));
		byte[] rawKey = RandomKey();

		ProtectedKeyMaterial a = protector.Protect(rawKey, Context());
		ProtectedKeyMaterial b = protector.Protect(rawKey, Context());
		Assert.NotEqual(a.Nonce, b.Nonce);
		Assert.NotEqual(a.Ciphertext, b.Ciphertext);
	}

	[Theory]
	[InlineData("different-owner", "msg-1", KeyProtectionPurposes.MessageContentKey, KeyProtectionFormatVersions.V1)]
	[InlineData("chat-1", "different-key", KeyProtectionPurposes.MessageContentKey, KeyProtectionFormatVersions.V1)]
	[InlineData("chat-1", "msg-1", "different-purpose", KeyProtectionFormatVersions.V1)]
	[InlineData("chat-1", "msg-1", KeyProtectionPurposes.MessageContentKey, "2")]
	public void Unprotect_RejectsContextSubstitution(string ownerContextId, string keyId, string purpose, string formatVersion)
	{
		using var dir = new TestMasterSecretDirectory();
		dir.WriteVersion("v1");
		var protector = new AesGcmMessageKeyProtector(new FileMasterSecretProvider(dir.OptionsFor("v1")));
		ProtectedKeyMaterial material = protector.Protect(RandomKey(), Context());

		Assert.Throws<KeyProtectionIntegrityException>(() => protector.Unprotect(material, Context(ownerContextId, keyId, purpose, formatVersion)));
	}

	[Fact]
	public void Unprotect_RejectsTamperedCiphertext()
	{
		using var dir = new TestMasterSecretDirectory();
		dir.WriteVersion("v1");
		var protector = new AesGcmMessageKeyProtector(new FileMasterSecretProvider(dir.OptionsFor("v1")));
		ProtectedKeyMaterial material = protector.Protect(RandomKey(), Context());
		material.Ciphertext[0] ^= 0xFF;

		Assert.Throws<KeyProtectionIntegrityException>(() => protector.Unprotect(material, Context()));
	}

	[Fact]
	public void Unprotect_RejectsTamperedTag()
	{
		using var dir = new TestMasterSecretDirectory();
		dir.WriteVersion("v1");
		var protector = new AesGcmMessageKeyProtector(new FileMasterSecretProvider(dir.OptionsFor("v1")));
		ProtectedKeyMaterial material = protector.Protect(RandomKey(), Context());
		material.Tag[0] ^= 0xFF;

		Assert.Throws<KeyProtectionIntegrityException>(() => protector.Unprotect(material, Context()));
	}

	[Fact]
	public void Unprotect_RejectsTamperedNonce()
	{
		using var dir = new TestMasterSecretDirectory();
		dir.WriteVersion("v1");
		var protector = new AesGcmMessageKeyProtector(new FileMasterSecretProvider(dir.OptionsFor("v1")));
		ProtectedKeyMaterial material = protector.Protect(RandomKey(), Context());
		material.Nonce[0] ^= 0xFF;

		Assert.Throws<KeyProtectionIntegrityException>(() => protector.Unprotect(material, Context()));
	}

	[Fact]
	public void Unprotect_RejectsWhenMasterKeyVersionChangedWithoutRewrap()
	{
		using var dir = new TestMasterSecretDirectory();
		dir.WriteVersion("v1");
		dir.WriteVersion("v2");
		var protector = new AesGcmMessageKeyProtector(new FileMasterSecretProvider(dir.OptionsFor("v1")));
		ProtectedKeyMaterial material = protector.Protect(RandomKey(), Context());

		// Simulate an attacker (or a bug) relabeling the stored version without re-encrypting.
		var relabeled = new ProtectedKeyMaterial("v2", material.Nonce, material.Ciphertext, material.Tag);
		Assert.Throws<KeyProtectionIntegrityException>(() => protector.Unprotect(relabeled, Context()));
	}

	[Fact]
	public void Unprotect_Throws_WhenMasterSecretMissing()
	{
		using var dir = new TestMasterSecretDirectory();
		dir.WriteVersion("v1");
		var provider = new FileMasterSecretProvider(dir.OptionsFor("v1"));
		var protector = new AesGcmMessageKeyProtector(provider);
		ProtectedKeyMaterial material = protector.Protect(RandomKey(), Context());

		dir.DeleteVersion("v1");
		// A fresh provider (simulating a restart with the secret unmounted) must fail closed.
		var freshProviderButMissingSecret = new TestMasterSecretDirectory();
		Assert.Throws<MasterSecretUnavailableException>(() => new AesGcmMessageKeyProtector(new FileMasterSecretProvider(freshProviderButMissingSecret.OptionsFor("v1"))));
		freshProviderButMissingSecret.Dispose();
	}
}
