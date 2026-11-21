namespace Database.Entities.Sync;

/// <summary>
/// One trusted device's approval of ONE exact pending registration (plan/06-recovery.md: "Одобрението
/// се обвързва с точната заявка и публичен ключ"). The approving device and the registering device have
/// no channel between them, so the signature is carried here, keyed by the very challenge it signs -
/// the row IS the binding, not a message that merely mentions one.
///
/// Everything the approval commits to is copied here from the server-side challenge row at approval
/// time (<see cref="PendingDeviceRegistrationId"/>, <see cref="PendingKeyId"/>,
/// <see cref="PendingPublicKeySpki"/>, <see cref="PendingLocalInstanceId"/>,
/// <see cref="ExpiresAtUtc"/>), and re-compared against that same challenge at register time: a grant can
/// therefore never be re-pointed at a different device, key, local copy or lifetime than the one the
/// approver actually saw. <see cref="ConsumedAtUtc"/> is set by a single conditional UPDATE
/// ("... WHERE ConsumedAtUtc IS NULL"), exactly like <see cref="SyncDeviceChallengeEntity.ConsumedAtUtc"/>,
/// so a concurrent replay of the same register attempt can never consume it twice.
///
/// <see cref="ApprovingSessionId"/> is stored, but is NOT authority on its own - per
/// docs/sync-contract-v1.md ("Validate trust at commit; supplied ApprovingSession is not authority") the
/// approver's device trust AND this exact session's liveness are re-verified under row locks inside the
/// registration commit (<see cref="Services.Sessions.Interface.IDeviceTrustService.TryTrustPendingDeviceAsync"/>).
/// </summary>
public class SyncDeviceApprovalGrantEntity
{
	public SyncDeviceApprovalGrantEntity(
		string challengeId, string accountId, string pendingDeviceRegistrationId, string pendingKeyId,
		byte[] pendingPublicKeySpki, string pendingLocalInstanceId, string approvingDeviceRegistrationId,
		string approvingSessionId, byte[] signature, DateTime expiresAtUtc, DateTime createdAtUtc)
	{
		ChallengeId = challengeId;
		AccountId = accountId;
		PendingDeviceRegistrationId = pendingDeviceRegistrationId;
		PendingKeyId = pendingKeyId;
		PendingPublicKeySpki = pendingPublicKeySpki;
		PendingLocalInstanceId = pendingLocalInstanceId;
		ApprovingDeviceRegistrationId = approvingDeviceRegistrationId;
		ApprovingSessionId = approvingSessionId;
		Signature = signature;
		ExpiresAtUtc = expiresAtUtc;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncDeviceApprovalGrantEntity() { }

	/// <summary>Primary key: one grant per pending request, because the challenge itself is one-use.</summary>
	public string ChallengeId { get; set; } = null!;

	public string AccountId { get; set; } = null!;

	public string PendingDeviceRegistrationId { get; set; } = null!;

	public string PendingKeyId { get; set; } = null!;

	/// <summary>The exact DER SubjectPublicKeyInfo the approver signed over - compared byte-for-byte with the challenge's own key at register time, so a requester cannot swap keys after being approved.</summary>
	public byte[] PendingPublicKeySpki { get; set; } = null!;

	/// <summary>The requesting local SQLite copy this approval was granted for; register refuses any other.</summary>
	public string PendingLocalInstanceId { get; set; } = null!;

	public string ApprovingDeviceRegistrationId { get; set; } = null!;

	/// <summary>Diagnostics/commit-time re-validation target - see this type's own doc comment for why it is not authority.</summary>
	public string ApprovingSessionId { get; set; } = null!;

	/// <summary>64-byte IEEE P1363 R||S ES256 signature over the pinned chat-device-approval-v1 array.</summary>
	public byte[] Signature { get; set; } = null!;

	/// <summary>Copied from the challenge: an approval can never outlive the request it approves.</summary>
	public DateTime ExpiresAtUtc { get; set; }

	public DateTime CreatedAtUtc { get; set; }

	/// <summary>Null until the registration it authorizes commits; see this type's own doc comment for the atomic claim.</summary>
	public DateTime? ConsumedAtUtc { get; set; }

	public virtual SyncAccountEntity Account { get; set; } = null!;
	public virtual SyncDeviceChallengeEntity Challenge { get; set; } = null!;
}
