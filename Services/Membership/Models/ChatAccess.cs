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
/// <param name="MembershipId">
/// Stage 08: the stint this window belongs to - the identity an archive's frozen metadata/status
/// snapshots are keyed by (docs/sync-contract-v1.md: "Resolve archive metadata/status at the named
/// snapshot, not today's state"). Null only for the unprojected-legacy-chat fallback, which has no
/// stint at all.
/// </param>
/// <param name="IsFrozen">Whether the owning stint has ended, i.e. whether this window is a read-only archive whose content must be resolved at its snapshot rather than at today's state.</param>
public sealed record LegacyHistoryWindow(DateTime? FromUtc, DateTime? ThroughUtc, string? MembershipId = null, bool IsFrozen = false)
{
	public bool Contains(DateTime createdAtUtc) =>
		(FromUtc is null || createdAtUtc >= FromUtc) && (ThroughUtc is null || createdAtUtc <= ThroughUtc);

	/// <summary>
	/// This window clipped to <paramref name="fromUtc"/>..<paramref name="throughUtc"/>, or null when the
	/// two do not overlap. Stage 08's sharing rule is exactly this operation applied to the adder's own
	/// windows: "Споделя се пресечната част на избрания период и историята, разрешена на добавящия.
	/// Недостъпните за него междини остават недостъпни" - an adder's gap produces no clipped window, so
	/// it is never filled in for the added member either.
	/// </summary>
	public (DateTime From, DateTime Through)? ClipTo(DateTime fromUtc, DateTime throughUtc)
	{
		DateTime from = FromUtc is null || FromUtc < fromUtc ? fromUtc : FromUtc.Value;
		DateTime through = ThroughUtc is null || ThroughUtc > throughUtc ? throughUtc : ThroughUtc.Value;

		return through < from ? null : (from, through);
	}
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
/// <param name="PersonalCorrespondenceSuspended">
/// Stage 09: this is a PERSONAL chat whose two people may not create new correspondence right now -
/// either side blocked the other, or they are no longer contacts ("Блокиране или премахване на контакт
/// прекратява новата лична кореспонденция"). It suspends <see cref="CanSendNewMessages"/> only:
/// <see cref="CanRead"/> and <see cref="CanSend"/> stay exactly as they were, because the permitted old
/// messages must remain readable AND keep receiving status updates. Always false for a group - "Личното
/// блокиране запазва активното участие и кореспонденцията в общи групи".
/// </param>
public sealed record ChatAccess(
	ChatAccessState State, IReadOnlyList<LegacyHistoryWindow> Windows, bool PersonalCorrespondenceSuspended = false)
{
	public static readonly ChatAccess Denied = new(ChatAccessState.None, []);

	/// <summary>An unprojected legacy chat the caller is a member of: everything, as before stage 07.</summary>
	public static readonly ChatAccess UnboundedActive = new(ChatAccessState.Active, [new LegacyHistoryWindow(null, null)]);

	/// <summary>
	/// An ACTIVE stint: may act in this chat at all - mark Seen, register presence, be acknowledged.
	/// False for a frozen (read-only archive) stint. Deliberately NOT affected by a personal block: the
	/// status traffic of the messages that already exist must continue
	/// (<see cref="CanSendNewMessages"/> is the gate for creating new ones).
	/// </summary>
	public bool CanSend => State == ChatAccessState.Active;

	/// <summary>
	/// May create NEW content here: an active stint whose personal correspondence is not suspended. This
	/// is the only gate that a block/removal closes.
	/// </summary>
	public bool CanSendNewMessages => CanSend && !PersonalCorrespondenceSuspended;

	/// <summary>May read something in this chat - current content or just a frozen archive.</summary>
	public bool CanRead => State != ChatAccessState.None;

	/// <summary>
	/// The per-message gate: whether this caller may receive/acknowledge the message created at
	/// <paramref name="createdAtUtc"/>, and - once stage 11-13 hand out key grants - its message key.
	/// A member added later never reaches back before their own window, and a removed member never
	/// reaches past the moment of their removal.
	/// </summary>
	public bool PermitsMessageAt(DateTime createdAtUtc) => WindowAt(createdAtUtc) is not null;

	/// <summary>
	/// Stage 08: WHICH window covers a message, which is what decides how its statuses are resolved.
	/// A still-open (non-frozen) window always wins over a frozen one covering the same instant -
	/// "Новосподеленият период получава текущото разрешено състояние, включително при припокриване",
	/// while "Старият архив извън новосподеления период остава замразен". Among frozen windows the
	/// latest-starting one wins, so the most recent archive describes the overlap.
	/// </summary>
	public LegacyHistoryWindow? WindowAt(DateTime createdAtUtc)
	{
		LegacyHistoryWindow? best = null;
		for (int i = 0; i < Windows.Count; i++)
		{
			LegacyHistoryWindow window = Windows[i];
			if (!window.Contains(createdAtUtc))
			{
				continue;
			}

			if (best is null
				|| (best.IsFrozen && !window.IsFrozen)
				|| (best.IsFrozen == window.IsFrozen && (window.FromUtc ?? DateTime.MinValue) > (best.FromUtc ?? DateTime.MinValue)))
			{
				best = window;
			}
		}

		return best;
	}
}
