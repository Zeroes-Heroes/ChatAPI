using Database.Entities.Sync;
using Services.Keys;
using Services.Keys.Models;
using Services.Keys.Service;
using Xunit;

namespace KeyModuleTests;

public class MessageKeyProvisioningServiceTests
{
	private static byte[] RandomKey()
	{
		byte[] key = new byte[32];
		System.Security.Cryptography.RandomNumberGenerator.Fill(key);
		return key;
	}

	private static (MessageKeyProvisioningService Service, TestMasterSecretDirectory Dir) MakeService(string currentVersion = "v1")
	{
		var dir = new TestMasterSecretDirectory();
		dir.WriteVersion(currentVersion);
		var protector = new AesGcmMessageKeyProtector(new FileMasterSecretProvider(dir.OptionsFor(currentVersion)));
		return (new MessageKeyProvisioningService(protector), dir);
	}

	[Fact]
	public void ProtectForStorage_BuildsAnEntityThatUnwrapsBackToTheRawKey()
	{
		(MessageKeyProvisioningService service, TestMasterSecretDirectory dir) = MakeService();
		using (dir)
		{
			byte[] rawKey = RandomKey();
			SyncMessageKeyEntity entity = service.ProtectForStorage("msg-1", "chat-1", rawKey, DateTime.UtcNow);

			Assert.Equal("msg-1", entity.MessageId);
			Assert.Equal("chat-1", entity.ChatId);
			Assert.Equal(KeyProtectionPurposes.MessageContentKey, entity.Purpose);
			Assert.Equal(KeyProtectionFormatVersions.V1, entity.FormatVersion);
			Assert.Equal("v1", entity.KeyVersion);

			byte[] recovered = service.UnwrapForAuthorizedDelivery(entity, new MessageKeyAccessContext("msg-1", "chat-1"));
			Assert.Equal(rawKey, recovered);
		}
	}

	[Theory]
	[InlineData("different-message", "chat-1")]
	[InlineData("msg-1", "different-chat")]
	public void UnwrapForAuthorizedDelivery_RejectsContextNotMatchingTheStoredRow(string messageId, string chatId)
	{
		(MessageKeyProvisioningService service, TestMasterSecretDirectory dir) = MakeService();
		using (dir)
		{
			SyncMessageKeyEntity entity = service.ProtectForStorage("msg-1", "chat-1", RandomKey(), DateTime.UtcNow);
			Assert.Throws<KeyContextMismatchException>(() => service.UnwrapForAuthorizedDelivery(entity, new MessageKeyAccessContext(messageId, chatId)));
		}
	}

	[Fact]
	public void UnwrapForAuthorizedDelivery_DoesNotEvenAttemptCryptoWhenContextMismatches()
	{
		// The context check must happen before any AES-GCM call - this is what makes the method not
		// a disguised "decrypt by id" primitive. Corrupt the entity's ciphertext so that if crypto
		// were attempted it would throw KeyProtectionIntegrityException instead of the expected
		// KeyContextMismatchException, proving the context check runs first.
		(MessageKeyProvisioningService service, TestMasterSecretDirectory dir) = MakeService();
		using (dir)
		{
			SyncMessageKeyEntity entity = service.ProtectForStorage("msg-1", "chat-1", RandomKey(), DateTime.UtcNow);
			entity.ProtectedKey[0] ^= 0xFF;
			Assert.Throws<KeyContextMismatchException>(() => service.UnwrapForAuthorizedDelivery(entity, new MessageKeyAccessContext("wrong-message", "chat-1")));
		}
	}

	[Fact]
	public void Rewrap_PreservesMessageIdChatIdAndRawKey_ButChangesTheProtectedBytesAndVersion()
	{
		var dir = new TestMasterSecretDirectory();
		dir.WriteVersion("v1");
		var protectorV1 = new AesGcmMessageKeyProtector(new FileMasterSecretProvider(dir.OptionsFor("v1")));
		var serviceV1 = new MessageKeyProvisioningService(protectorV1);

		byte[] rawKey = RandomKey();
		SyncMessageKeyEntity entity = serviceV1.ProtectForStorage("msg-1", "chat-1", rawKey, DateTime.UtcNow);
		byte[] originalCiphertext = (byte[])entity.ProtectedKey.Clone();

		// "Deploy" a new master version while keeping v1 mounted (so existing rows stay readable).
		dir.WriteVersion("v2");
		var protectorV2 = new AesGcmMessageKeyProtector(new FileMasterSecretProvider(dir.OptionsFor("v2")));
		var serviceV2 = new MessageKeyProvisioningService(protectorV2);

		SyncMessageKeyEntity rewrapped = serviceV2.Rewrap(entity, new MessageKeyAccessContext("msg-1", "chat-1"));

		Assert.Same(entity, rewrapped);
		Assert.Equal("msg-1", rewrapped.MessageId);
		Assert.Equal("chat-1", rewrapped.ChatId);
		Assert.Equal("v2", rewrapped.KeyVersion);
		Assert.NotEqual(originalCiphertext, rewrapped.ProtectedKey);

		byte[] recovered = serviceV2.UnwrapForAuthorizedDelivery(rewrapped, new MessageKeyAccessContext("msg-1", "chat-1"));
		Assert.Equal(rawKey, recovered);

		dir.Dispose();
	}

	[Fact]
	public void Rewrap_RejectsContextMismatch_WithoutMutatingTheEntity()
	{
		(MessageKeyProvisioningService service, TestMasterSecretDirectory dir) = MakeService();
		using (dir)
		{
			SyncMessageKeyEntity entity = service.ProtectForStorage("msg-1", "chat-1", RandomKey(), DateTime.UtcNow);
			byte[] originalCiphertext = (byte[])entity.ProtectedKey.Clone();
			string originalVersion = entity.KeyVersion;

			Assert.Throws<KeyContextMismatchException>(() => service.Rewrap(entity, new MessageKeyAccessContext("wrong-message", "chat-1")));

			Assert.Equal(originalCiphertext, entity.ProtectedKey);
			Assert.Equal(originalVersion, entity.KeyVersion);
		}
	}
}
