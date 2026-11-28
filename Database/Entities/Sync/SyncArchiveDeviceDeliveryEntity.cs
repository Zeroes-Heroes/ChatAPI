namespace Database.Entities.Sync;

/// <summary>
/// Private per-device progress for a frozen archive: this exact SQLite copy
/// (<see cref="LocalInstanceId"/>) has downloaded that archived message under that membership stint.
///
/// MESSAGE_SYNC_ANALYSIS.md: "Техническото потвърждение за сваляне на архив на ново устройство
/// обновява частния device sync запис. Публичните отметки и unread в замразения чат остават
/// непроменени." Nothing public is ever derived from this table - it is read only to decide what a
/// given device still has to fetch, which is also what makes restoring an archive on a newly
/// authorized device terminate instead of repeating forever.
///
/// Keyed by LocalInstanceId rather than DeviceRegistrationId because clearing local data creates a new
/// instance that genuinely has to download the archive again (docs/sync-contract-v1.md: "LocalInstanceId
/// denotes one SQLite copy; clearing it requires full sync").
/// </summary>
public class SyncArchiveDeviceDeliveryEntity
{
	public SyncArchiveDeviceDeliveryEntity(string membershipId, string localInstanceId, int legacyMessageId, DateTime storedAtUtc)
	{
		MembershipId = membershipId;
		LocalInstanceId = localInstanceId;
		LegacyMessageId = legacyMessageId;
		StoredAtUtc = storedAtUtc;
	}

	private SyncArchiveDeviceDeliveryEntity() { }

	public string MembershipId { get; set; } = null!;

	public string LocalInstanceId { get; set; } = null!;

	public int LegacyMessageId { get; set; }

	public DateTime StoredAtUtc { get; set; }

	public virtual SyncMembershipEntity Membership { get; set; } = null!;
	public virtual SyncLocalInstanceEntity LocalInstance { get; set; } = null!;
	public virtual MessageEntity Message { get; set; } = null!;
}
