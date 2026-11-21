namespace Database.Entities.Sync;

/// <summary>
/// Physical per-device status for one message (ciphertext stored / received / observed-seen),
/// distinct from the logical per-person SyncMessageRecipientStateEntity. Composite FKs chain
/// LocalInstance -> DeviceRegistration -> Account and Membership -> (Chat, Account), so a device
/// state row can only exist for a device that truly belongs to the stated account, a membership
/// that truly belongs to both that account and that chat, and a message that truly belongs to that chat.
/// </summary>
public class SyncMessageDeviceStateEntity
{
	public SyncMessageDeviceStateEntity(
		string messageId, string chatId, string deviceRegistrationId, string localInstanceId,
		string accountId, string membershipId, long version)
	{
		MessageId = messageId;
		ChatId = chatId;
		DeviceRegistrationId = deviceRegistrationId;
		LocalInstanceId = localInstanceId;
		AccountId = accountId;
		MembershipId = membershipId;
		Version = version;
	}

	private SyncMessageDeviceStateEntity() { }

	public string MessageId { get; set; } = null!;

	/// <summary>Denormalized from Message/Membership for the composite FKs.</summary>
	public string ChatId { get; set; } = null!;

	public string DeviceRegistrationId { get; set; } = null!;

	public string LocalInstanceId { get; set; } = null!;

	/// <summary>Denormalized from DeviceRegistration/Membership for the composite FKs.</summary>
	public string AccountId { get; set; } = null!;

	public string MembershipId { get; set; } = null!;

	public long Version { get; set; }

	public DateTime? CiphertextStoredAtUtc { get; set; }

	public DateTime? ReceivedAtUtc { get; set; }

	public DateTime? ObservedSeenAtUtc { get; set; }

	public virtual SyncMessageEntity Message { get; set; } = null!;
	public virtual SyncDeviceRegistrationEntity DeviceRegistration { get; set; } = null!;
	public virtual SyncLocalInstanceEntity LocalInstance { get; set; } = null!;
	public virtual SyncMembershipEntity Membership { get; set; } = null!;
}
