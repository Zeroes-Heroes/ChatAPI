using Database.Enums.Sync;

namespace Services.Statuses.Models;

/// <summary>
/// The ONE public fact about (message, person), folded from however many per-stint rows that person
/// holds for that message. The status module owns what a status MEANS, so both readers of the public
/// mark - the sync projection and the frozen-archive capture - answer from this single fold rather
/// than from two mappings that could drift.
///
/// More than one contributing row is normal rather than a defect: <c>SyncMessageRecipientStates</c> is
/// keyed by (MessageId, MembershipId), so a rejoin gives the same person a SECOND stint and a second
/// row for the same message. The v1 receipt surface does not create one (it writes only while no row of
/// that person carries a <c>ReceivedAtUtc</c>), but the OwnSend pass is not bound by that guard:
/// <c>MessageStatusService.SyncRecipientStateAsync</c> resolves - and creates - the CURRENT stint's row
/// for every older incoming message that send marks Seen, and its "is this already Seen" test is itself
/// per stint. A rejoined person can therefore legitimately end up with an archive row and a current row
/// for one message, and the public answer has to be one state derived from both rather than whichever
/// row a query happened to return first.
/// </summary>
/// <param name="Version">
/// The aggregate's own version: the SUM of the contributing rows' versions. That is what makes it
/// monotone. Every row's <c>Version</c> only ever increments, no writer deletes a row, and a newly
/// created row starts at 0 - so the sum never decreases, and it moves whenever ANY contributing row
/// moves. The alternatives do not hold: an arbitrary single row's version regresses outright as soon as
/// the chosen row changes, and a maximum silently swallows a change made on the other row (two rows at
/// 7, one goes to 8, the maximum was already 8 if the other had moved first).
/// </param>
public readonly record struct PublicRecipientState(
	long Version,
	DateTime? ReceivedAtUtc,
	DateTime? SeenAtUtc,
	SyncSeenReason? SeenReason)
{
	/// <summary>
	/// Folds one person's rows for one message. The EARLIEST proof of each fact wins, which is the rule
	/// the status model already states everywhere else: "the first device that proves it owns the
	/// moment" for Received, and "the first accepted Seen transition fixes reason and time for good" for
	/// Seen - so a later stint repeating a mark never moves a recorded time, and a person's public state
	/// never goes backwards because a newer stint has not repeated an older mark.
	///
	/// This is a read-side fold over timestamps that are immutable once set. It mutates nothing, it
	/// invents no Seen (a mark is only taken when it carries its reason), and an empty set folds to the
	/// explicit "nothing is known" state, which is the same answer the single-row readers gave before.
	/// </summary>
	public static PublicRecipientState Fold(IEnumerable<PublicRecipientState> rows)
	{
		long version = 0;
		DateTime? received = null;
		DateTime? seenAt = null;
		SyncSeenReason? seenReason = null;

		foreach (PublicRecipientState row in rows)
		{
			// No writer can produce a negative version (every row starts at 0 and only increments); the
			// clamp keeps one corrupted row from dragging the whole aggregate backwards.
			version += Math.Max(row.Version, 0);

			if (row.ReceivedAtUtc is DateTime receivedAt && (received is null || receivedAt < received))
			{
				received = receivedAt;
			}

			// A Seen instant without its reason is not a usable public mark - a projection would have to
			// invent one - so only complete pairs take part, and the pair always travels together.
			if (row.SeenAtUtc is not DateTime candidateAt || row.SeenReason is not SyncSeenReason candidateReason)
			{
				continue;
			}

			if (seenAt is null
				|| candidateAt < seenAt.Value
				// Equal instants are ordered by the reason's own value, so the answer cannot depend on
				// the order the rows happened to be read in.
				|| (candidateAt == seenAt.Value && candidateReason < seenReason!.Value))
			{
				seenAt = candidateAt;
				seenReason = candidateReason;
			}
		}

		return new PublicRecipientState(version, received, seenAt, seenReason);
	}
}
