namespace Database.Entities.Sync;

/// <summary>
/// One chat scope of a sync session's FIXED boundary set, captured in the handshake's own transaction.
/// The set is both the enumeration SyncBoundaryPage pages through and the catalog's own fixed view, so
/// a chat added after the handshake simply is not in it: "A later rights change invalidates this
/// enumeration rather than mixing snapshots."
///
/// Ordinal is the enumeration order (priority scopes first, in priority order), which is what gives the
/// boundary pages and the Full-mode catalog pages a stable, resumable sequence without a second key.
/// </summary>
public class SyncSessionBoundaryEntity
{
	public SyncSessionBoundaryEntity(
		string syncSessionId, int ordinal, string chatId, string membershipId, long rightsVersion,
		long throughChangeSequence, bool isFrozen)
	{
		SyncSessionId = syncSessionId;
		Ordinal = ordinal;
		ChatId = chatId;
		MembershipId = membershipId;
		RightsVersion = rightsVersion;
		ThroughChangeSequence = throughChangeSequence;
		IsFrozen = isFrozen;
	}

	private SyncSessionBoundaryEntity() { }

	public string SyncSessionId { get; set; } = null!;

	public int Ordinal { get; set; }

	public string ChatId { get; set; } = null!;

	/// <summary>The stint this boundary belongs to: a rejoin's archive and its new period are two boundaries, never one merged view.</summary>
	public string MembershipId { get; set; } = null!;

	public long RightsVersion { get; set; }

	/// <summary>
	/// The fixed upper bound in the chat's change-sequence space: the chat's last committed change for an
	/// active stint, and the stint's own FrozenAtChangeSequence for a frozen one - an archive cannot grow,
	/// so its boundary is the moment it was frozen.
	/// </summary>
	public long ThroughChangeSequence { get; set; }

	public bool IsFrozen { get; set; }

	/// <summary>1..10 for the priority set this handshake fixed, null for the rest. Also the ordering key behind <see cref="Ordinal"/>.</summary>
	public int? PriorityRank { get; set; }

	/// <summary>The position this device uploaded for this stream in its Resume exchange; null until it did.</summary>
	public long? UploadedSequence { get; set; }

	/// <summary>The position acknowledged as continuously applied; this scope counts as data-ready only once it reaches <see cref="ThroughChangeSequence"/>.</summary>
	public long? AcknowledgedSequence { get; set; }

	public DateTime? AcknowledgedAtUtc { get; set; }

	public virtual SyncSessionEntity Session { get; set; } = null!;
	public virtual SyncChatEntity Chat { get; set; } = null!;
	public virtual SyncMembershipEntity Membership { get; set; } = null!;
}
