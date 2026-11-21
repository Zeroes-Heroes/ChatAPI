namespace Database.Entities.Sync;

/// <summary>
/// Authorship material handed from the approving trusted device to one exact pending registration, so
/// plan/06-recovery.md's second alternative ("одобрение от доверено устройство") restores the ORIGINAL
/// author keys without the recovery secret. Sealed on the approver's device to the requester's own
/// device public key (ephemeral ECDH P-256 + HKDF-SHA256 + AES-256-GCM); the server stores opaque
/// ciphertext and can never read it. Keyed by the challenge it belongs to, exactly like
/// <see cref="SyncDeviceApprovalGrantEntity"/>, and delivered one-use to that same device only.
/// </summary>
public class SyncAuthorKeyHandoffEntity
{
	public SyncAuthorKeyHandoffEntity(
		string challengeId, string accountId, string pendingDeviceRegistrationId,
		byte[] ephemeralPublicKeySpki, byte[] nonce, byte[] ciphertext, byte[] tag, DateTime createdAtUtc)
	{
		ChallengeId = challengeId;
		AccountId = accountId;
		PendingDeviceRegistrationId = pendingDeviceRegistrationId;
		EphemeralPublicKeySpki = ephemeralPublicKeySpki;
		Nonce = nonce;
		Ciphertext = ciphertext;
		Tag = tag;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncAuthorKeyHandoffEntity() { }

	public string ChallengeId { get; set; } = null!;

	public string AccountId { get; set; } = null!;

	/// <summary>The only device this handoff may ever be delivered to.</summary>
	public string PendingDeviceRegistrationId { get; set; } = null!;

	public byte[] EphemeralPublicKeySpki { get; set; } = null!;

	public byte[] Nonce { get; set; } = null!;

	public byte[] Ciphertext { get; set; } = null!;

	public byte[] Tag { get; set; } = null!;

	public DateTime CreatedAtUtc { get; set; }

	/// <summary>Set by a single conditional UPDATE on first delivery; a replayed fetch gets nothing.</summary>
	public DateTime? DeliveredAtUtc { get; set; }

	public virtual SyncAccountEntity Account { get; set; } = null!;
	public virtual SyncDeviceApprovalGrantEntity Grant { get; set; } = null!;
}
