using Database.Enums.Sync;

namespace Database.Entities.Sync;

/// <summary>
/// Per-account catalog journal, the "cursor" stream for StreamKind.Catalog. CatalogSequence is
/// allocated by SyncSequenceAllocator from the account's own counter row, which is a distinct
/// ordering domain from any chat's ChangeSequence (cross-chat positions are not comparable to each
/// other, only within one account's catalog). Same pointer-log shape and pruning caveat as
/// SyncChatChangeEntity.
/// </summary>
public class SyncCatalogEntryEntity
{
	public SyncCatalogEntryEntity(string accountId, long catalogSequence, string chatId, SyncChangeKind kind, DateTime occurredAtUtc)
	{
		AccountId = accountId;
		CatalogSequence = catalogSequence;
		ChatId = chatId;
		Kind = kind;
		OccurredAtUtc = occurredAtUtc;
	}

	private SyncCatalogEntryEntity() { }

	public string AccountId { get; set; } = null!;

	public long CatalogSequence { get; set; }

	public string ChatId { get; set; } = null!;

	public SyncChangeKind Kind { get; set; }

	public string? ReferenceId { get; set; }

	public DateTime OccurredAtUtc { get; set; }

	public virtual SyncAccountEntity Account { get; set; } = null!;
	public virtual SyncChatEntity Chat { get; set; } = null!;
}
