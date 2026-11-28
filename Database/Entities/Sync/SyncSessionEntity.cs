using Database.Enums.Sync;

namespace Database.Entities.Sync;

/// <summary>
/// One handshake-fixed sync session (the wire contract's SyncSessionId). Everything a page needs after
/// the handshake lives here and in SyncSessionBoundaries rather than in a replica's memory, which is
/// what lets the handshake happen on one API replica and every subsequent HTTP page on another
/// ("HTTP може да се обслужи от друга реплика чрез общото устойчиво състояние").
///
/// The row is written once, inside the handshake's own transaction, together with its boundary rows:
/// a reader therefore never observes a session whose fixed set is still being written. Positions
/// uploaded and acknowledged afterwards are the only mutable part.
/// </summary>
public class SyncSessionEntity
{
	public SyncSessionEntity(
		string id, string accountId, string deviceRegistrationId, string localInstanceId, string deviceSessionId,
		string connectionId, SyncSessionMode mode, long rightsVersion, long catalogThroughSequence,
		string snapshotId, DateTime createdAtUtc, DateTime expiresAtUtc)
	{
		Id = id;
		AccountId = accountId;
		DeviceRegistrationId = deviceRegistrationId;
		LocalInstanceId = localInstanceId;
		DeviceSessionId = deviceSessionId;
		ConnectionId = connectionId;
		Mode = mode;
		RightsVersion = rightsVersion;
		CatalogThroughSequence = catalogThroughSequence;
		SnapshotId = snapshotId;
		CreatedAtUtc = createdAtUtc;
		ExpiresAtUtc = expiresAtUtc;
	}

	private SyncSessionEntity() { }

	public string Id { get; set; } = null!;

	public string AccountId { get; set; } = null!;

	public string DeviceRegistrationId { get; set; } = null!;

	public string LocalInstanceId { get; set; } = null!;

	/// <summary>The device session this sync session is bound to; its revocation ends this one too.</summary>
	public string DeviceSessionId { get; set; } = null!;

	/// <summary>
	/// The live addressing established before the boundaries were fixed. Temporary by contract
	/// ("ConnectionId/SyncSessionId are temporary and never replace operation identity"): a reconnect
	/// produces a new connection and therefore a new handshake, and an acknowledgement bound to an older
	/// connection can no longer make anything ready.
	/// </summary>
	public string ConnectionId { get; set; } = null!;

	public SyncSessionMode Mode { get; set; }

	/// <summary>
	/// The account-scoped rights version this session was fixed at: the account's catalog sequence at
	/// handshake. Every membership grant/freeze appends a catalog entry for the account, so this value
	/// moves exactly when access does - including when old history is newly shared, which is what makes
	/// such a grant invalidate the session instead of waiting for a cursor to reach it.
	/// </summary>
	public long RightsVersion { get; set; }

	/// <summary>The fixed upper bound of the catalog stream (the account's last committed catalog sequence).</summary>
	public long CatalogThroughSequence { get; set; }

	/// <summary>Names the immutable view every boundary and cursor of this session is bound to.</summary>
	public string SnapshotId { get; set; } = null!;

	public DateTime CreatedAtUtc { get; set; }

	/// <summary>Cursor lifetime follows the sync session; expiry preserves applied positions, it only forces a new handshake.</summary>
	public DateTime ExpiresAtUtc { get; set; }

	public DateTime? EndedAtUtc { get; set; }

	/// <summary>The catalog position this device uploaded in its Resume exchange; null until it did.</summary>
	public long? CatalogUploadedSequence { get; set; }

	/// <summary>The catalog position acknowledged as continuously applied; readiness requires it to reach <see cref="CatalogThroughSequence"/>.</summary>
	public long? CatalogAcknowledgedSequence { get; set; }

	/// <summary>
	/// Set when a Resume exchange finished with Final=true after every earlier index. "The server records
	/// the completed resume set before declaring readiness; partial uploads cannot complete initial
	/// restoration" - so this is a precondition of readiness, never a consequence of one page.
	/// </summary>
	public DateTime? ResumeCompletedAtUtc { get; set; }

	/// <summary>How many chat boundaries were fixed, so readiness can be decided without counting rows.</summary>
	public int BoundaryCount { get; set; }

	public virtual SyncAccountEntity Account { get; set; } = null!;
	public virtual SyncDeviceRegistrationEntity DeviceRegistration { get; set; } = null!;
	public virtual SyncLocalInstanceEntity LocalInstance { get; set; } = null!;
	public virtual SyncDeviceSessionEntity DeviceSession { get; set; } = null!;
	public virtual ICollection<SyncSessionBoundaryEntity> Boundaries { get; set; } = [];
}
