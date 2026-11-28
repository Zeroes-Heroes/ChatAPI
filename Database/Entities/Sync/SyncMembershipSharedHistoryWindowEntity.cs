using Database.Enums.Sync;

namespace Database.Entities.Sync;

/// <summary>
/// One disjoint window of <b>older</b> history that was shared with a membership stint at the moment
/// it was added (plan/08-history-archives.md). A stint's whole permission is its participation window
/// (<see cref="SyncMembershipEntity.LegacyHistoryFromUtc"/> .. <see cref="SyncMembershipEntity.EndedAtUtc"/>)
/// plus every row here; several rows exist exactly when the adder's own history had inaccessible gaps
/// inside the chosen period, which stay inaccessible ("Недостъпните за него междини остават недостъпни").
///
/// Why not a <see cref="SyncMembershipHistoryGrantEntity"/> row: that table is MessageSequence space
/// over <c>SyncMessages</c> (stage 11+) and its <c>FromMessageSequence &gt; 0</c> check cannot represent
/// a window over legacy <c>Messages</c>, which carry no sequence at all. The same add still writes a
/// sequence-space grant whenever the window actually contains <c>SyncMessages</c>, so the two stay
/// consistent; this row is the legacy-stream counterpart, exactly as
/// <see cref="SyncMembershipEntity.LegacyHistoryFromUtc"/> is for the participation window.
/// </summary>
public class SyncMembershipSharedHistoryWindowEntity
{
	public SyncMembershipSharedHistoryWindowEntity(
		string membershipId, DateTime fromUtc, DateTime throughUtc,
		SyncSharedHistoryPeriod selectedPeriod, long grantedAtChangeSequence, DateTime grantedAtUtc)
	{
		MembershipId = membershipId;
		FromUtc = fromUtc;
		ThroughUtc = throughUtc;
		SelectedPeriod = selectedPeriod;
		GrantedAtChangeSequence = grantedAtChangeSequence;
		GrantedAtUtc = grantedAtUtc;
	}

	private SyncMembershipSharedHistoryWindowEntity() { }

	public long Id { get; set; }

	public string MembershipId { get; set; } = null!;

	/// <summary>Inclusive lower bound: the later of the chosen period's start and the start of the adder's own permitted range this window was cut from.</summary>
	public DateTime FromUtc { get; set; }

	/// <summary>Inclusive upper bound. Never open: a shared window is always closed at the add (anything after that is the participation window's business).</summary>
	public DateTime ThroughUtc { get; set; }

	public SyncSharedHistoryPeriod SelectedPeriod { get; set; }

	/// <summary>The chat change sequence of the Added event that granted this window, so the grant is locatable in the chat's own journal.</summary>
	public long GrantedAtChangeSequence { get; set; }

	public DateTime GrantedAtUtc { get; set; }

	public virtual SyncMembershipEntity Membership { get; set; } = null!;
}
