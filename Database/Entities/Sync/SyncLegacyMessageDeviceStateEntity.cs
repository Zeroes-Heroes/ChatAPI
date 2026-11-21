namespace Database.Entities.Sync;

/// <summary>
/// The PHYSICAL progress of one SQLite copy for one legacy message - the stage-10 counterpart of
/// <see cref="SyncMessageDeviceStateEntity"/> over the legacy <c>Messages</c> table.
///
/// Three distinct facts, deliberately not collapsed into one
/// (plan/10-recipient-status.md: "Разделят се записан ciphertext, готовност за четене и реално
/// наблюдавано Seen на устройство от логическото състояние на получателя"):
/// <list type="bullet">
/// <item><see cref="CiphertextStoredAtUtc"/> - the package is on disk, but not yet readable (no key /
/// not yet verified). Alone it never produces a logical Received.</item>
/// <item><see cref="ReceivedAtUtc"/> - durable write plus, from stage 11, an available key and a
/// successful signature/decrypt check.</item>
/// <item><see cref="ObservedSeenAtUtc"/> - this device really observed the message. The person's
/// logical Seen is a separate row and keeps its first accepted time.</item>
/// </list>
///
/// Keyed by LocalInstanceId (one SQLite copy), which is what makes "Ново устройство доказва собствената
/// си доставка" true: a newly authorized device - or the same device after its data was cleared - has
/// no row here even though the person's public marks say the message arrived long ago.
/// </summary>
public class SyncLegacyMessageDeviceStateEntity
{
	public SyncLegacyMessageDeviceStateEntity(
		int legacyMessageId, string localInstanceId, string deviceRegistrationId,
		string accountId, string membershipId, string chatId, long version)
	{
		LegacyMessageId = legacyMessageId;
		LocalInstanceId = localInstanceId;
		DeviceRegistrationId = deviceRegistrationId;
		AccountId = accountId;
		MembershipId = membershipId;
		ChatId = chatId;
		Version = version;
	}

	private SyncLegacyMessageDeviceStateEntity() { }

	public int LegacyMessageId { get; set; }

	public string LocalInstanceId { get; set; } = null!;

	public string DeviceRegistrationId { get; set; } = null!;

	/// <summary>Denormalized from Membership for the composite FK below; the owner of the device and of the stint must be the same account.</summary>
	public string AccountId { get; set; } = null!;

	/// <summary>The stint under which this device holds the message - an archive restore records progress against the frozen stint it is restoring.</summary>
	public string MembershipId { get; set; } = null!;

	/// <summary>Denormalized from Membership for the composite FK below.</summary>
	public string ChatId { get; set; } = null!;

	public long Version { get; set; }

	public DateTime? CiphertextStoredAtUtc { get; set; }

	public DateTime? ReceivedAtUtc { get; set; }

	public DateTime? ObservedSeenAtUtc { get; set; }

	public virtual SyncLocalInstanceEntity LocalInstance { get; set; } = null!;
	public virtual SyncMembershipEntity Membership { get; set; } = null!;
	public virtual MessageEntity Message { get; set; } = null!;
}
