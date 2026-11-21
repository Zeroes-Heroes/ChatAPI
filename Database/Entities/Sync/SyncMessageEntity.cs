using Database.Enums.Sync;

namespace Database.Entities.Sync;

/// <summary>
/// One encrypted message. The server never sees plaintext: SignedPackage is the opaque signed,
/// encrypted wire payload exactly as produced by the client; PackageDigest lets retries of the
/// same clientOperationId be recognized as identical or conflicting without re-parsing it.
/// AuthorKeyId/AuthorKeyVersion plus (SenderAccountId, ChatId) together mirror the wire contract's
/// AuthorKeyReference; actual author-key issuance/verification storage belongs to the crypto stage.
/// </summary>
public class SyncMessageEntity
{
	public SyncMessageEntity(
		string id, string chatId, string senderAccountId, string senderMembershipId,
		long messageSequence, long changeSequence, SyncOperationKind operationKind,
		string authorKeyId, string authorKeyVersion, string signedPackage, string packageDigest,
		DateTime createdAtUtc)
	{
		Id = id;
		ChatId = chatId;
		SenderAccountId = senderAccountId;
		SenderMembershipId = senderMembershipId;
		MessageSequence = messageSequence;
		ChangeSequence = changeSequence;
		OperationKind = operationKind;
		AuthorKeyId = authorKeyId;
		AuthorKeyVersion = authorKeyVersion;
		SignedPackage = signedPackage;
		PackageDigest = packageDigest;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncMessageEntity() { }

	public string Id { get; set; } = null!;

	public string ChatId { get; set; } = null!;

	public string SenderAccountId { get; set; } = null!;

	/// <summary>The sender's own membership stint at send time; ties the message to a specific (ChatId, AccountId) membership via composite FKs, not just a bare account id.</summary>
	public string SenderMembershipId { get; set; } = null!;

	/// <summary>Chat-scoped, gap-free ordering among messages only. Allocated together with ChangeSequence by SyncSequenceAllocator.</summary>
	public long MessageSequence { get; set; }

	/// <summary>Chat-scoped ordering shared with all other change kinds in the chat (membership, unread, ...).</summary>
	public long ChangeSequence { get; set; }

	public SyncOperationKind OperationKind { get; set; }

	public string AuthorKeyId { get; set; } = null!;

	public string AuthorKeyVersion { get; set; } = null!;

	public string SignedPackage { get; set; } = null!;

	public string PackageDigest { get; set; } = null!;

	public DateTime CreatedAtUtc { get; set; }

	public virtual SyncChatEntity Chat { get; set; } = null!;
	public virtual SyncAccountEntity SenderAccount { get; set; } = null!;
	public virtual SyncMembershipEntity SenderMembership { get; set; } = null!;
	public virtual SyncMessageKeyEntity? Key { get; set; }
}
