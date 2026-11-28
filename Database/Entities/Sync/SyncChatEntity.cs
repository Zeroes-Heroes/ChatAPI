using Database.Enums.Sync;

namespace Database.Entities.Sync;

public class SyncChatEntity
{
	public SyncChatEntity(string id, SyncChatKind kind, string? name, DateTime createdAtUtc)
	{
		Id = id;
		Kind = kind;
		Name = name;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncChatEntity() { }

	public string Id { get; set; } = null!;

	public SyncChatKind Kind { get; set; }

	public string? Name { get; set; }

	public DateTime CreatedAtUtc { get; set; }

	public virtual SyncChatSequenceCounterEntity? SequenceCounter { get; set; }
	public virtual ICollection<SyncMembershipEntity> Memberships { get; set; } = [];
	public virtual ICollection<SyncMessageEntity> Messages { get; set; } = [];
}
