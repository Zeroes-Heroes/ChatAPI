namespace Database.Entities.Sync;

/// <summary>Identity for the sync protocol's LocalInstanceId, i.e. one device's local SQLite copy of the data.</summary>
public class SyncLocalInstanceEntity
{
	public SyncLocalInstanceEntity(string id, string deviceRegistrationId, DateTime createdAtUtc)
	{
		Id = id;
		DeviceRegistrationId = deviceRegistrationId;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncLocalInstanceEntity() { }

	public string Id { get; set; } = null!;

	public string DeviceRegistrationId { get; set; } = null!;

	public DateTime CreatedAtUtc { get; set; }

	public virtual SyncDeviceRegistrationEntity DeviceRegistration { get; set; } = null!;
}
