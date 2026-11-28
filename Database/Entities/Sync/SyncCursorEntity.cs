using Database.Enums.Sync;

namespace Database.Entities.Sync;

/// <summary>
/// The durable state behind one opaque page cursor: the token is 256 random bits and carries no
/// meaning at all, so this row is the whole of it - which is what lets another API replica continue the
/// same enumeration.
///
/// The token is stored as it was issued (<see cref="Id"/>) rather than hashed, unlike a refresh token,
/// because a cursor is explicitly NOT authority: a page revalidates the session, the device, the
/// current rights and the snapshot before this row is even consulted, so possession alone grants
/// nothing. The idempotent retry needs it: re-answering an already-answered cursor must hand back the
/// SAME continuation token (see <see cref="NextCursorId"/>), which a one-way hash could not reproduce.
///
/// <see cref="IssuedThroughSequence"/>/<see cref="NextCursorId"/> are what make a page idempotent: the
/// first successful read memoizes the exact range it answered and the continuation it minted, so a
/// retry after a lost response returns the same items and the same next cursor instead of a page that
/// shifted because a status changed in between.
/// </summary>
public class SyncCursorEntity
{
	public SyncCursorEntity(
		string id, string syncSessionId, string accountId, string deviceRegistrationId, string localInstanceId,
		SyncCursorSurface surface, SyncStreamKind streamKind, long rightsVersion, string snapshotId,
		long fromSequence, long upperBoundSequence, DateTime createdAtUtc, DateTime expiresAtUtc)
	{
		Id = id;
		SyncSessionId = syncSessionId;
		AccountId = accountId;
		DeviceRegistrationId = deviceRegistrationId;
		LocalInstanceId = localInstanceId;
		Surface = surface;
		StreamKind = streamKind;
		RightsVersion = rightsVersion;
		SnapshotId = snapshotId;
		FromSequence = fromSequence;
		UpperBoundSequence = upperBoundSequence;
		CreatedAtUtc = createdAtUtc;
		ExpiresAtUtc = expiresAtUtc;
	}

	private SyncCursorEntity() { }

	/// <summary>The opaque token itself: canonical unpadded base64url of 32 random bytes (43 ASCII characters, far inside the contract's 2048-byte cursor cap).</summary>
	public string Id { get; set; } = null!;

	public string SyncSessionId { get; set; } = null!;

	public string AccountId { get; set; } = null!;

	public string DeviceRegistrationId { get; set; } = null!;

	public string LocalInstanceId { get; set; } = null!;

	public SyncCursorSurface Surface { get; set; }

	public SyncStreamKind StreamKind { get; set; }

	/// <summary>Null exactly for a catalog/boundary cursor; both set for a chat stream.</summary>
	public string? ChatId { get; set; }

	public string? MembershipId { get; set; }

	public long RightsVersion { get; set; }

	public string SnapshotId { get; set; } = null!;

	/// <summary>Forward is the only direction v1 pages in; stored because the contract binds a cursor to its direction.</summary>
	public bool Forward { get; set; } = true;

	/// <summary>Inclusive start of this cursor's own page, in this surface's own sequence space (change, catalog, message or ordinal).</summary>
	public long FromSequence { get; set; }

	/// <summary>The fixed upper bound this enumeration may never pass, even if the stream has grown since.</summary>
	public long UpperBoundSequence { get; set; }

	/// <summary>Set once this cursor has answered: the exact inclusive end it issued.</summary>
	public long? IssuedThroughSequence { get; set; }

	/// <summary>The continuation minted for the page this cursor answered, null when that page was terminal.</summary>
	public string? NextCursorId { get; set; }

	/// <summary>True once this cursor has answered a page whose Complete was true.</summary>
	public bool IsTerminal { get; set; }

	public DateTime CreatedAtUtc { get; set; }

	public DateTime ExpiresAtUtc { get; set; }

	public virtual SyncSessionEntity Session { get; set; } = null!;
}
