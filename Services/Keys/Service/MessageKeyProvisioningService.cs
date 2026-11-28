using System.Security.Cryptography;
using Database.Entities.Sync;
using Services.Keys.Interface;
using Services.Keys.Models;

namespace Services.Keys.Service;

public sealed class MessageKeyProvisioningService(IMessageKeyProtector protector) : IMessageKeyProvisioningService
{
	public SyncMessageKeyEntity ProtectForStorage(string messageId, string chatId, ReadOnlySpan<byte> rawMessageKey, DateTime createdAtUtc)
	{
		var context = new KeyProtectionContext(KeyProtectionFormatVersions.V1, KeyProtectionPurposes.MessageContentKey, chatId, messageId);
		ProtectedKeyMaterial material = protector.Protect(rawMessageKey, context);

		return new SyncMessageKeyEntity(
			messageId, chatId, context.FormatVersion, context.Purpose, material.MasterKeyVersion,
			material.Ciphertext, material.Nonce, material.Tag, createdAtUtc);
	}

	public byte[] UnwrapForAuthorizedDelivery(SyncMessageKeyEntity row, MessageKeyAccessContext expectedContext)
	{
		RequireMatchingContext(row, expectedContext);
		KeyProtectionContext context = ContextOf(row);
		var material = new ProtectedKeyMaterial(row.KeyVersion, row.Nonce, row.ProtectedKey, row.Tag);
		return protector.Unprotect(material, context);
	}

	public SyncMessageKeyEntity Rewrap(SyncMessageKeyEntity row, MessageKeyAccessContext expectedContext)
	{
		byte[] rawKey = UnwrapForAuthorizedDelivery(row, expectedContext);
		try
		{
			KeyProtectionContext context = ContextOf(row);
			ProtectedKeyMaterial material = protector.Protect(rawKey, context);

			row.KeyVersion = material.MasterKeyVersion;
			row.Nonce = material.Nonce;
			row.ProtectedKey = material.Ciphertext;
			row.Tag = material.Tag;
			return row;
		}
		finally
		{
			// This provisioner's own temporary plaintext key, spanning unwrap->rewrap - only the
			// re-protected row is returned to the caller, never this raw key, so it's safe/owned to
			// zero here (unlike `UnwrapForAuthorizedDelivery`'s own return value, which the caller
			// owns and must clear itself once actually used for delivery).
			CryptographicOperations.ZeroMemory(rawKey);
		}
	}

	private static void RequireMatchingContext(SyncMessageKeyEntity row, MessageKeyAccessContext expectedContext)
	{
		if (!string.Equals(row.MessageId, expectedContext.MessageId, StringComparison.Ordinal) ||
			!string.Equals(row.ChatId, expectedContext.ChatId, StringComparison.Ordinal))
		{
			throw new KeyContextMismatchException("Expected (messageId, chatId) does not match the protected key row.");
		}
	}

	private static KeyProtectionContext ContextOf(SyncMessageKeyEntity row) =>
		new(row.FormatVersion, row.Purpose, row.ChatId, row.MessageId);
}
