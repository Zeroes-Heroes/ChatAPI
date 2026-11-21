namespace Database.Entities.Sync;

/// <summary>
/// The message's random content key, wrapped (encrypted) under the server's key-protection module
/// (<c>Services.Keys</c>, plan 04) with AES-256-GCM. 1:1 with the message and written in the same
/// transaction, per the accepted decisions. <see cref="FormatVersion"/>, <see cref="KeyVersion"/>
/// (the master secret version), <see cref="Purpose"/>, <see cref="ChatId"/> (owner context) and
/// <see cref="MessageId"/> (key id) are all bound into the AES-GCM associated data by
/// Services.Keys.Service.AesGcmMessageKeyProtector - see docs/key-module.md - so that copying a
/// valid (Nonce, ProtectedKey, Tag) triple onto a different row fails authentication instead of
/// silently decrypting under the wrong context.
/// </summary>
public class SyncMessageKeyEntity
{
	public SyncMessageKeyEntity(
		string messageId, string chatId, string formatVersion, string purpose, string keyVersion,
		byte[] protectedKey, byte[] nonce, byte[] tag, DateTime createdAtUtc)
	{
		MessageId = messageId;
		ChatId = chatId;
		FormatVersion = formatVersion;
		Purpose = purpose;
		KeyVersion = keyVersion;
		ProtectedKey = protectedKey;
		Nonce = nonce;
		Tag = tag;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncMessageKeyEntity() { }

	public string MessageId { get; set; } = null!;

	/// <summary>The message's chat; the AAD "owner context" binding this protected key to its chat's authorization scope.</summary>
	public string ChatId { get; set; } = null!;

	/// <summary>Version of this row's own storage envelope shape, independent of <see cref="KeyVersion"/>; lets the key module evolve its on-disk format without a data migration that touches ProtectedKey/Nonce/Tag.</summary>
	public string FormatVersion { get; set; } = null!;

	/// <summary>What this protected key is for, e.g. "message-content-key"; part of the AAD so a record cannot be reinterpreted for a different purpose.</summary>
	public string Purpose { get; set; } = null!;

	/// <summary>Version of the server master/data key used to protect this record; independent of membership RightsVersion. Rewrap updates this (and Nonce/ProtectedKey/Tag) in place; old versions stay readable by the master secret provider until explicitly retired.</summary>
	public string KeyVersion { get; set; } = null!;

	public byte[] ProtectedKey { get; set; } = null!;

	public byte[] Nonce { get; set; } = null!;

	public byte[] Tag { get; set; } = null!;

	public DateTime CreatedAtUtc { get; set; }

	public virtual SyncMessageEntity Message { get; set; } = null!;
}
