namespace Database.Entities.Sync;

/// <summary>
/// One device's live session, per docs/sync-contract-v1.md's SessionContext/SessionId and "Sessions
/// are stored per device" (plan research). <see cref="TrustVersionAtIssuance"/> is the device's
/// <see cref="SyncDeviceRegistrationEntity.TrustVersion"/> snapshotted when this session was minted;
/// every HTTP/hub current-trust check (<see cref="Services.Sessions.Interface.ISessionTrustChecker"/>)
/// compares the access token's own embedded trust version against the device's CURRENT TrustVersion,
/// not this column - this column exists for audit/diagnostics (what epoch did issuance happen under),
/// not as the live authority. <see cref="CurrentRefreshTokenHash"/>/<see cref="RefreshTokenExpiresAtUtc"/>
/// are the only durable refresh secret state kept at rest: per docs/sync-contract-v1.md, "Refresh
/// tokens are opaque random secrets, hashed at rest" - the raw secret itself is never stored here (see
/// <see cref="SyncRefreshTokenRotationEntity"/> for the short-lived, encrypted exception that makes
/// idempotent retries possible without ever persisting it unprotected).
/// </summary>
public class SyncDeviceSessionEntity
{
	public SyncDeviceSessionEntity(
		string id, string accountId, string deviceRegistrationId, string localInstanceId, DateTime createdAtUtc,
		long trustVersionAtIssuance, byte[] currentRefreshTokenHash, DateTime? refreshTokenExpiresAtUtc)
	{
		Id = id;
		AccountId = accountId;
		DeviceRegistrationId = deviceRegistrationId;
		LocalInstanceId = localInstanceId;
		CreatedAtUtc = createdAtUtc;
		TrustVersionAtIssuance = trustVersionAtIssuance;
		CurrentRefreshTokenHash = currentRefreshTokenHash;
		RefreshTokenExpiresAtUtc = refreshTokenExpiresAtUtc;
	}

	private SyncDeviceSessionEntity() { }

	/// <summary>The wire SessionId.</summary>
	public string Id { get; set; } = null!;

	public string AccountId { get; set; } = null!;

	public string DeviceRegistrationId { get; set; } = null!;

	/// <summary>
	/// The wire DeviceContext's LocalInstanceId, fixed for the lifetime of this session (set once at
	/// device-register, carried unchanged through every refresh rotation - RefreshSessionRequest has
	/// no field to change it, per docs/sync-contract-v1.md). A genuinely new local copy (local data
	/// wipe) requires a new session via reauthentication, not a mutation of this column - out of
	/// scope for this stage (plan/20-logout.md territory), noted in the stage report.
	/// </summary>
	public string LocalInstanceId { get; set; } = null!;

	public DateTime CreatedAtUtc { get; set; }

	public long TrustVersionAtIssuance { get; set; }

	/// <summary>Set (never cleared back to null while <see cref="EndedAtUtc"/> is null) by explicit revocation, a detected refresh-token-reuse (family revocation) or a later logout stage. Terminal: an ended session is never reactivated - reauthentication creates a new session.</summary>
	public DateTime? EndedAtUtc { get; set; }

	/// <summary>SHA-256 of the current raw opaque refresh-token secret. Compared with a constant-time comparison (<see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals"/>) in <see cref="Services.Sessions.Interface.IRefreshTokenService"/>, never a plain <c>==</c>/SQL-index equality alone for the decisive accept check. Null once no further refresh is possible (ended session).</summary>
	public byte[]? CurrentRefreshTokenHash { get; set; }

	public DateTime? RefreshTokenExpiresAtUtc { get; set; }

	/// <summary>The RefreshOperationId of the rotation that produced <see cref="CurrentRefreshTokenHash"/>, or null if this session has never rotated. Lets a lookup distinguish "retry of the rotation that produced the CURRENT token" without a second table scan.</summary>
	public string? LastRotationOperationId { get; set; }

	public DateTime? LastRotationAtUtc { get; set; }

	public virtual SyncAccountEntity Account { get; set; } = null!;
	public virtual SyncDeviceRegistrationEntity DeviceRegistration { get; set; } = null!;
	public virtual SyncLocalInstanceEntity LocalInstance { get; set; } = null!;
	public virtual ICollection<SyncRefreshTokenRotationEntity> Rotations { get; set; } = [];
}
