using Database.Enums.Sync;

namespace Services.Chat.Models.Membership;

/// <summary>Stage 07 group composition requests. Chat ids are the legacy ids every current client surface speaks; the service maps them to the sync model itself.</summary>
public class AddChatMembersRequest
{
	public int ChatId { get; set; }

	public int[] UserIds { get; set; } = [];

	/// <summary>
	/// Stage 08: the period the adder chose to share. Absent/unknown means
	/// <see cref="SyncSharedHistoryPeriod.None"/> - no previous history - so a client that does not send
	/// it keeps stage 07's behaviour instead of silently widening anyone's access.
	/// </summary>
	public SyncSharedHistoryPeriod SharedHistory { get; set; } = SyncSharedHistoryPeriod.None;

	/// <summary>
	/// The adder's current offset east of UTC, in minutes, used only to resolve
	/// <see cref="SyncSharedHistoryPeriod.CurrentCalendarDay"/> ("календарния ден до добавянето според
	/// неговата часова зона"). The boundary itself is always computed on the server, from the server
	/// clock; this is the one thing only the device knows.
	/// </summary>
	public int TimeZoneOffsetMinutes { get; set; }
}

public class RemoveChatMemberRequest
{
	public int ChatId { get; set; }

	public int UserId { get; set; }
}

public class LeaveChatRequest
{
	public int ChatId { get; set; }
}

/// <summary>Stage 08: the device's confirmation that it has durably stored these archived messages (`POST /chat/history-stored`).</summary>
public class ConfirmArchiveStoredRequest
{
	public int ChatId { get; set; }

	public int[] MessageIds { get; set; } = [];
}
