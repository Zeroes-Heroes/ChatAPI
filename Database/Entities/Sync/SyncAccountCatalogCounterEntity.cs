using Database.Sequencing;

namespace Database.Entities.Sync;

/// <summary>
/// Holds the next catalog sequence for one account. Must only be advanced through
/// Database.Sequencing.SyncSequenceAllocator, which uses an atomic UPDATE ... RETURNING so the
/// row lock is held for the whole allocating transaction: no later catalog position can be
/// allocated until the earlier one has committed or rolled back.
///
/// Seeded at Database.Sequencing.SyncSequenceConventions.FirstSequence (1); see that type for why
/// 0 must stay reserved as the "nothing applied yet" sentinel.
/// </summary>
public class SyncAccountCatalogCounterEntity
{
	public SyncAccountCatalogCounterEntity(string accountId)
	{
		AccountId = accountId;
		NextCatalogSequence = SyncSequenceConventions.FirstSequence;
	}

	private SyncAccountCatalogCounterEntity() { }

	public string AccountId { get; set; } = null!;

	public long NextCatalogSequence { get; set; }

	public virtual SyncAccountEntity Account { get; set; } = null!;
}
