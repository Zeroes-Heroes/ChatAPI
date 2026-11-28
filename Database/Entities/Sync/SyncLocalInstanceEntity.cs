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

	/// <summary>
	/// Stage 13: the durable initial-restore marker for this exact SQLite copy. Null means every
	/// handshake for it is still Full mode - "Full mode persists until the account/local-instance initial
	/// restore marker completes, including after restart" - and clearing the local instance (a new copy
	/// gets a new id, and therefore a null marker here) is what makes a fresh database require a full
	/// restoration again even on a known device registration.
	/// </summary>
	public DateTime? InitialRestoreCompletedAtUtc { get; set; }

	public virtual SyncDeviceRegistrationEntity DeviceRegistration { get; set; } = null!;
}
