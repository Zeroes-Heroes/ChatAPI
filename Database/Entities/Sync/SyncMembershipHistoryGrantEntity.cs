namespace Database.Entities.Sync;

/// <summary>
/// One disjoint range of message history an account is allowed to read under a given membership
/// stint. A stint can carry several of these (e.g. after rejoin: the frozen archive range from the
/// old stint and the newly shared range from the new one), which is how rejoin gaps are represented.
///
/// Wire mapping for the contract's HistoryRange.StateSnapshotId (docs/sync-contract-v1.md's
/// "Membership snapshots carry disjoint permitted ranges, snapshot IDs and optional frozen change
/// boundaries"): this entity does not store a separate StateSnapshotId column. For a frozen range
/// (FrozenAtChangeSequence set), the snapshot identity IS this grant's own MembershipId - the same
/// stint id that SyncMembershipMetadataSnapshotEntity and SyncMembershipMessageStatusSnapshotEntity
/// are already keyed and FK'd by, so the service layer serializes StateSnapshotId as MembershipId
/// for frozen ranges rather than inventing a second, unconstrained identity for the same thing. A
/// still-open range (FrozenAtChangeSequence null) has no frozen snapshot to name; there is no
/// "live fixed view" snapshot concept anywhere else in this schema yet, so no storage column
/// claims to provide one here - defining and persisting that identity is a later stage's decision
/// once a live-view snapshot mechanism actually exists. See docs/server-data-migrations.md for the
/// full writeup of this decision.
/// </summary>
public class SyncMembershipHistoryGrantEntity
{
	public SyncMembershipHistoryGrantEntity(string membershipId, long fromMessageSequence, long? throughMessageSequence)
	{
		MembershipId = membershipId;
		FromMessageSequence = fromMessageSequence;
		ThroughMessageSequence = throughMessageSequence;
	}

	private SyncMembershipHistoryGrantEntity() { }

	public long Id { get; set; }

	public string MembershipId { get; set; } = null!;

	public long FromMessageSequence { get; set; }

	/// <summary>Null means "through the current end of the chat's allowed history for this stint".</summary>
	public long? ThroughMessageSequence { get; set; }

	/// <summary>Set when this range was frozen at a specific chat change position (removal), null while still open/current.</summary>
	public long? FrozenAtChangeSequence { get; set; }

	public virtual SyncMembershipEntity Membership { get; set; } = null!;
}
