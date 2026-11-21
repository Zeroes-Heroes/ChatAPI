using Database.Enums.Sync;

namespace Database.Entities.Sync;

/// <summary>
/// Immutable, scoped record of a terminal operation outcome, keyed exactly like the wire
/// contract's OperationIdentity (account + device registration + local instance + clientOperationId).
/// Only Accepted/Rejected are persisted here; Unknown/Retryable are computed responses, never stored.
/// Retrying the same clientOperationId with a different PackageDigest is how a later stage detects
/// a conflicting resend; this table only provides the storage for that comparison.
/// </summary>
public class SyncOperationResultEntity
{
	public SyncOperationResultEntity(
		string accountId, string deviceRegistrationId, string localInstanceId, string clientOperationId,
		string packageDigest, SyncOperationOutcome outcome, DateTime createdAtUtc)
	{
		AccountId = accountId;
		DeviceRegistrationId = deviceRegistrationId;
		LocalInstanceId = localInstanceId;
		ClientOperationId = clientOperationId;
		PackageDigest = packageDigest;
		Outcome = outcome;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncOperationResultEntity() { }

	public string AccountId { get; set; } = null!;

	public string DeviceRegistrationId { get; set; } = null!;

	public string LocalInstanceId { get; set; } = null!;

	public string ClientOperationId { get; set; } = null!;

	public string PackageDigest { get; set; } = null!;

	public SyncOperationOutcome Outcome { get; set; }

	public string? ChatId { get; set; }

	public string? MessageId { get; set; }

	public SyncOperationErrorCode? ErrorCode { get; set; }

	public DateTime CreatedAtUtc { get; set; }

	public virtual SyncAccountEntity Account { get; set; } = null!;
	public virtual SyncDeviceRegistrationEntity DeviceRegistration { get; set; } = null!;
	public virtual SyncLocalInstanceEntity LocalInstance { get; set; } = null!;
	public virtual SyncMessageEntity? Message { get; set; }
}
