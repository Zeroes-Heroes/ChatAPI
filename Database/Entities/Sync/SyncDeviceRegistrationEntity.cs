namespace Database.Entities.Sync;

/// <summary>Identity for the sync protocol's DeviceRegistrationId. Trust/revocation lifecycle belongs to a later stage; this is only the identity anchor.</summary>
public class SyncDeviceRegistrationEntity
{
	public SyncDeviceRegistrationEntity(string id, string accountId, DateTime createdAtUtc)
	{
		Id = id;
		AccountId = accountId;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncDeviceRegistrationEntity() { }

	public string Id { get; set; } = null!;

	public string AccountId { get; set; } = null!;

	public DateTime CreatedAtUtc { get; set; }

	public virtual SyncAccountEntity Account { get; set; } = null!;
	public virtual ICollection<SyncLocalInstanceEntity> LocalInstances { get; set; } = [];
}
