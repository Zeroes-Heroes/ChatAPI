using Database.Enums.Sync;

namespace Database.Entities.Sync;

/// <summary>
/// Identity for the sync protocol's DeviceRegistrationId, extended by stage 05
/// (plan/05-device-sessions.md) with the actual trust lifecycle stage 02 deliberately deferred
/// ("Trust/revocation lifecycle belongs to a later stage; this is only the identity anchor" - this
/// stage's doc comment it supersedes). A row is created in <see cref="DeviceTrustState.Pending"/> the
/// moment an enrollment/approval challenge binds a concrete DeviceRegistrationId to a public key -
/// see <see cref="Services.Sessions.Interface.IDeviceChallengeService"/> - and only ever becomes
/// <see cref="DeviceTrustState.Trusted"/> at the device-register commit, under a locked
/// account row, after the device proof (and approval, if required) verify. <see cref="TrustVersion"/>
/// is the epoch every issued access/refresh token for this device embeds; revocation bumps it so a
/// token minted before revocation fails current-trust checks even while still cryptographically
/// valid and unexpired (plan/05-device-sessions.md: "HTTP и SignalR операциите проверяват сесията и
/// текущото доверие").
/// </summary>
public class SyncDeviceRegistrationEntity
{
	public SyncDeviceRegistrationEntity(
		string id, string accountId, DateTime createdAtUtc,
		string keyId, byte[] publicKeySpki, bool isFirstDevice, string? approvedByDeviceRegistrationId)
	{
		Id = id;
		AccountId = accountId;
		CreatedAtUtc = createdAtUtc;
		KeyId = keyId;
		PublicKeySpki = publicKeySpki;
		TrustState = DeviceTrustState.Pending;
		TrustVersion = 1;
		IsFirstDevice = isFirstDevice;
		ApprovedByDeviceRegistrationId = approvedByDeviceRegistrationId;
	}

	private SyncDeviceRegistrationEntity() { }

	public string Id { get; set; } = null!;

	public string AccountId { get; set; } = null!;

	public DateTime CreatedAtUtc { get; set; }

	/// <summary>Opaque id for <see cref="PublicKeySpki"/>, chosen by the client (the chat-device-proof-v1 array's own "keyId" field) - not a hardware identifier, per docs/sync-contract-v1.md: "DeviceRegistrationId denotes a registration with a verified possession key, not a hardware ID."</summary>
	public string KeyId { get; set; } = null!;

	/// <summary>DER SubjectPublicKeyInfo for the P-256 device key this registration is bound to. The server-authorized source of truth for every device-proof/refresh-proof verification - never trusted from a request body's own embedded key.</summary>
	public byte[] PublicKeySpki { get; set; } = null!;

	public DeviceTrustState TrustState { get; set; }

	/// <summary>The trust epoch embedded in every token minted for this device. Incremented on revoke; never decremented or reused.</summary>
	public long TrustVersion { get; set; }

	public DateTime? RevokedAtUtc { get; set; }

	/// <summary>True only for the registration that won the atomic first-device decision for its account (plan/05-device-sessions.md: "First device determination atomic/account-scoped"). Informational/audit - current enrollment decisions are always recomputed from live state (trusted-device count + <see cref="SyncAccountEntity.RevocationBarrierActive"/>), never from this flag on a prior row.</summary>
	public bool IsFirstDevice { get; set; }

	/// <summary>The trusted device whose chat-device-approval-v1 signature authorized this registration, if approval was required. Null for a genuine first device (no approval needed) and for this stage's recovery-secret path (06).</summary>
	public string? ApprovedByDeviceRegistrationId { get; set; }

	public virtual SyncAccountEntity Account { get; set; } = null!;
	public virtual ICollection<SyncLocalInstanceEntity> LocalInstances { get; set; } = [];
	public virtual SyncDeviceRegistrationEntity? ApprovedByDevice { get; set; }
}
