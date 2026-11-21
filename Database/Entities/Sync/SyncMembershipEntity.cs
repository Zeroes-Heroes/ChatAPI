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

	/// <summary>
	/// Stage 07: who added this member - the actor of the stint's own MembershipEvent.Added, the exact
	/// counterpart of <see cref="EndedByAccountId"/>. Null for a founding member (the chat's creation is
	/// the event, not an addition) and for a stint projected from a pre-stage-07 legacy chat, where no
	/// actor was ever recorded. Keeping the actor on the stint row is what lets the wire event's
	/// ActorAccountContextId be derived per recipient (it must be null for a Removed event delivered to
	/// the removed subject) without storing a second, unconstrained copy on the change journal.
	/// </summary>
	public string? AddedByAccountId { get; set; }

	/// <summary>
	/// Stage 07: the lower bound of the legacy message history this stint may read, i.e. the
	/// legacy-stream equivalent of a <see cref="SyncMembershipHistoryGrantEntity"/> range start while
	/// real messages still live in the legacy <c>Messages</c> table (sequence-numbered SyncMessages
	/// arrive in stage 11). Set to the add time for a member added after creation - the conservative
	/// "no previous history" default, since choosing a shared period is stage 08 - and null for a
	/// founding/projected stint, which means "from the beginning of this chat". The upper bound is
	/// <see cref="EndedAtUtc"/>: a frozen stint keeps exactly what existed at its removal.
	/// </summary>
	public DateTime? LegacyHistoryFromUtc { get; set; }

	public virtual SyncChatEntity Chat { get; set; } = null!;
	public virtual SyncAccountEntity Account { get; set; } = null!;
	public virtual SyncAccountEntity? EndedByAccount { get; set; }
	public virtual SyncAccountEntity? AddedByAccount { get; set; }
	public virtual ICollection<SyncMembershipHistoryGrantEntity> HistoryGrants { get; set; } = [];
	public virtual SyncMembershipMetadataSnapshotEntity? MetadataSnapshot { get; set; }
	public virtual SyncUnreadStateEntity? Unread { get; set; }
}
