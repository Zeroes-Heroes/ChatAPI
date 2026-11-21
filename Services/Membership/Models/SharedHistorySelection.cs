using Database.Enums.Sync;

namespace Services.Membership.Models;

/// <summary>
/// What an adder chose to share, and the one place the chosen option becomes an actual instant.
///
/// MESSAGE_SYNC_ANALYSIS.md: "„1 ден" означава последните 24 часа, „10 дни" — период назад от
/// добавянето, а „текущият ден" — календарния ден до добавянето според неговата часова зона.
/// Изчислената граница се запазва за всички устройства." So the boundary is resolved ONCE, here,
/// from the SERVER clock at the moment of the add - the client supplies only the chosen option and its
/// own UTC offset, never a boundary of its own, which is what keeps a client from widening its own
/// access by claiming an arbitrary start.
/// </summary>
/// <param name="Period">The option the adder chose.</param>
/// <param name="TimeZoneOffsetMinutes">
/// The adder's current offset east of UTC, in minutes - only <see cref="SyncSharedHistoryPeriod.CurrentCalendarDay"/>
/// uses it. Clamped to the real range of civil offsets (-14:00..+14:00); anything outside is treated as
/// UTC rather than refused, because an implausible device offset must not make an add fail.
/// </param>
public sealed record SharedHistorySelection(SyncSharedHistoryPeriod Period, int TimeZoneOffsetMinutes)
{
	/// <summary>Stage 07's behaviour, and what an omitted selection means: the member starts with no previous history.</summary>
	public static readonly SharedHistorySelection None = new(SyncSharedHistoryPeriod.None, 0);

	private const int MaxOffsetMinutes = 14 * 60;

	/// <summary>
	/// The inclusive start of the shared period, or null for <see cref="SyncSharedHistoryPeriod.None"/>
	/// (and for an unrecognized value, which is treated as None - sharing nothing is the safe reading of
	/// an option this server does not know). The upper bound is always the add itself.
	/// </summary>
	public DateTime? ResolveFromUtc(DateTime nowUtc) => Period switch
	{
		SyncSharedHistoryPeriod.Last24Hours => nowUtc.AddHours(-24),
		SyncSharedHistoryPeriod.LastTenDays => nowUtc.AddDays(-10),
		SyncSharedHistoryPeriod.CurrentCalendarDay => CurrentCalendarDayStart(nowUtc),
		_ => null,
	};

	/// <summary>
	/// Midnight of the adder's own current calendar day, expressed as a UTC instant: shift into the
	/// adder's local time, drop the time of day, shift back. A DST transition that happened earlier the
	/// same local day can move this by the transition's size, which is why the result is clamped into
	/// (now - 24h, now] - the calendar-day option can never reach further back than the 24-hour one, nor
	/// into the future if a device reports an offset it is not actually in.
	/// </summary>
	private DateTime CurrentCalendarDayStart(DateTime nowUtc)
	{
		int offset = Math.Clamp(TimeZoneOffsetMinutes, -MaxOffsetMinutes, MaxOffsetMinutes);
		DateTime localNow = nowUtc.AddMinutes(offset);
		DateTime localMidnight = localNow.Date;
		DateTime startUtc = localMidnight.AddMinutes(-offset);

		DateTime earliest = nowUtc.AddHours(-24);
		return startUtc < earliest ? earliest : (startUtc > nowUtc ? nowUtc : startUtc);
	}
}
