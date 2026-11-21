namespace Database.Entities.Sync;

/// <summary>
/// The message's random content key, wrapped (encrypted) under the server's key-protection module.
/// 1:1 with the message and written in the same transaction, per the accepted decisions. Storage
/// only: the module that unwraps/protects these (ChatAPI's isolated key module) is a later stage.
/// </summary>
public class SyncMessageKeyEntity
{
	public SyncMessageKeyEntity(string messageId, string keyVersion, byte[] protectedKey, byte[] nonce, byte[] tag, DateTime createdAtUtc)
	{
		MessageId = messageId;
		KeyVersion = keyVersion;
		ProtectedKey = protectedKey;
		Nonce = nonce;
		Tag = tag;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncMessageKeyEntity() { }

	public string MessageId { get; set; } = null!;

	/// <summary>Version of the server master/data key used to protect this record; independent of membership RightsVersion.</summary>
	public string KeyVersion { get; set; } = null!;

	public byte[] ProtectedKey { get; set; } = null!;

	public byte[] Nonce { get; set; } = null!;

	public byte[] Tag { get; set; } = null!;

	public DateTime CreatedAtUtc { get; set; }

	public virtual SyncMessageEntity Message { get; set; } = null!;
}
