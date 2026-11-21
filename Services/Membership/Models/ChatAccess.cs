namespace Services.Membership.Models;

/// <summary>What a given account context may do with a given chat right now.</summary>
public enum ChatAccessState
{
	/// <summary>No membership stint, ever - or a stint belonging to a superseded account context.</summary>
	None = 0,

	/// <summary>An active stint: may read current content and publish. Holds for the single remaining member of a group too (plan/07-membership.md).</summary>
	Active = 1,

	/// <summary>Only ended stints: read-only archive of what each stint permitted. Also what every member of a group with zero active members has.</summary>
	Frozen = 2,
}

/// <summary>
/// One permitted window of legacy message history. <c>null</c> bounds are open: a null
/// <paramref name="FromUtc"/> means "from the beginning of this chat" (a founding member, or a stint
/// projected from a pre-stage-07 legacy chat) and a null <paramref name="ThroughUtc"/> means "and
/// everything that is still to come" (an active stint).
///
/// This is the legacy-message-stream counterpart of the wire contract's HistoryRange, which is
/// expressed in MessageSequence space. Real messages still live in the legacy <c>Messages</c> table
/// (sequence-numbered SyncMessages start in stage 11), and those rows only carry
/// <c>CreatedAt</c> - so the authorization boundary that can actually be evaluated today is a time
/// window. The sequence ranges are persisted in parallel on
/// <see cref="Database.Entities.Sync.SyncMembershipHistoryGrantEntity"/> by the same writes.
/// </summary>
public sealed record LegacyHistoryWindow(DateTime? FromUtc, DateTime? ThroughUtc)
{
	public bool Contains(DateTime createdAtUtc) =>
		(FromUtc is null || createdAtUtc >= FromUtc) && (ThroughUtc is null || createdAtUtc <= ThroughUtc);
}

/// <summary>
/// The answer of <see cref="Interface.IChatAccessAuthority"/> - the one shared access check behind
/// HTTP, socket and deferred (push / reconnect sweep) delivery, per plan/07-membership.md: "Общите
/// проверки определят достъпа до съобщения и ключове за HTTP, socket и отложена доставка."
///
/// <paramref name="Windows"/> is the UNION of every stint's permitted window, which is how a rejoin
/// is represented: the old frozen archive plus the newly shared period, with the inaccessible gap in
/// between simply not covered by any window. It is empty exactly when
/// <paramref name="State"/> is <see cref="ChatAccessState.None"/>.
/// </summary>
public sealed record ChatAccess(ChatAccessState State, IReadOnlyList<LegacyHistoryWindow> Windows)
{
	public static readonly ChatAccess Denied = new(ChatAccessState.None, []);

	/// <summary>An unprojected legacy chat the caller is a member of: everything, as before stage 07.</summary>
	public static readonly ChatAccess UnboundedActive = new(ChatAccessState.Active, [new LegacyHistoryWindow(null, null)]);

	/// <summary>May publish new content into this chat. False for a frozen (read-only archive) stint.</summary>
	public bool CanSend => State == ChatAccessState.Active;

	/// <summary>May read something in this chat - current content or just a frozen archive.</summary>
	public bool CanRead => State != ChatAccessState.None;

	/// <summary>
	/// The per-message gate: whether this caller may receive/acknowledge the message created at
	/// <paramref name="createdAtUtc"/>, and - once stage 11-13 hand out key grants - its message key.
	/// A member added later never reaches back before their own window, and a removed member never
	/// reaches past the moment of their removal.
	/// </summary>
	public bool PermitsMessageAt(DateTime createdAtUtc)
	{
		for (int i = 0; i < Windows.Count; i++)
		{
			if (Windows[i].Contains(createdAtUtc))
			{
				return true;
			}
		}

		return false;
	}
}
