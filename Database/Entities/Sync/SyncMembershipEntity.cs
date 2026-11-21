using Database.Enums.Sync;

namespace Database.Entities.Sync;

/// <summary>
/// One join-to-leave "stint" of an account's membership in a chat. Rejoining creates a new row
/// with a new Id rather than reusing the old one, so the old stint can stay Frozen with its own
/// history grants (the archive) while the new stint gets its own current rights.
/// </summary>
public class SyncMembershipEntity
{
	public SyncMembershipEntity(string id, string chatId, string accountId, SyncMembershipState state, long rightsVersion, DateTime createdAtUtc)
	{
		Id = id;
		ChatId = chatId;
		AccountId = accountId;
		State = state;
		RightsVersion = rightsVersion;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncMembershipEntity() { }

	public string Id { get; set; } = null!;

	public string ChatId { get; set; } = null!;

	public string AccountId { get; set; } = null!;

	public SyncMembershipState State { get; set; }

	public long RightsVersion { get; set; }

	public DateTime CreatedAtUtc { get; set; }

	public DateTime? EndedAtUtc { get; set; }

	public SyncMembershipEndReason? EndReason { get; set; }

	/// <summary>Who removed this member; null when the member left on their own or the stint is still active.</summary>
	public string? EndedByAccountId { get; set; }

	public virtual SyncChatEntity Chat { get; set; } = null!;
	public virtual SyncAccountEntity Account { get; set; } = null!;
	public virtual SyncAccountEntity? EndedByAccount { get; set; }
	public virtual ICollection<SyncMembershipHistoryGrantEntity> HistoryGrants { get; set; } = [];
	public virtual SyncMembershipMetadataSnapshotEntity? MetadataSnapshot { get; set; }
	public virtual SyncUnreadStateEntity? Unread { get; set; }
}
