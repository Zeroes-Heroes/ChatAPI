using Database.Enums.Sync;

namespace Database.Entities.Sync;

/// <summary>
/// The LOGICAL status of one legacy message for one person - the stage-10 counterpart of
/// <see cref="SyncMessageRecipientStateEntity"/> over the legacy <c>Messages</c> table, which is where
/// every real message lives until stage 11 (the sequence-space table is keyed by a <c>SyncMessages</c>
/// id through composite foreign keys and cannot describe a legacy message at all - the same split
/// stage 08 already made for its status snapshots).
///
/// Keyed by the recipient's MembershipId, not by a bare account id: a rejoin is a new stint, so its
/// logical state starts clean while the old stint's row stands as the archive's own frozen value.
/// <see cref="SeenAtUtc"/>/<see cref="SeenReason"/> are fixed by the FIRST accepted transition
/// ("Първият успешно записан Seen преход фиксира окончателния час за съобщението и получателя") and are
/// never rewritten by a later device observation.
/// </summary>
public class SyncLegacyMessageRecipientStateEntity
{
	public SyncLegacyMessageRecipientStateEntity(string membershipId, int legacyMessageId, string chatId, string accountId, long version)
	{
		MembershipId = membershipId;
		LegacyMessageId = legacyMessageId;
		ChatId = chatId;
		AccountId = accountId;
		Version = version;
	}

	private SyncLegacyMessageRecipientStateEntity() { }

	/// <summary>The recipient's own membership stint - the access period this status belongs to.</summary>
	public string MembershipId { get; set; } = null!;

	public int LegacyMessageId { get; set; }

	/// <summary>Denormalized from Membership for the composite FK below, which pins this row to the stint's true chat.</summary>
	public string ChatId { get; set; } = null!;

	/// <summary>Denormalized from Membership; the person this status is about, never a device.</summary>
	public string AccountId { get; set; } = null!;

	/// <summary>Monotonic per row: incremented on every accepted change, and returned to the device as the receipt's StateVersion.</summary>
	public long Version { get; set; }

	/// <summary>
	/// Set when at least one of this person's devices proved a durable local write (and, from stage 11,
	/// a successful decrypt/verify). Never derived from presence or from the server having sent the
	/// message - "Received изисква устойчив запис, ключ и успешна криптографска проверка".
	/// </summary>
	public DateTime? ReceivedAtUtc { get; set; }

	public DateTime? SeenAtUtc { get; set; }

	public SyncSeenReason? SeenReason { get; set; }

	/// <summary>
	/// The client's own reported observation instant, kept separately from the authoritative
	/// <see cref="SeenAtUtc"/> ("Оригиналният клиентски час се пази отделно"). Null when the device
	/// reported none, or when the mark was not an observation at all (HistoryShared/OwnSend).
	/// </summary>
	public DateTime? SeenClientReportedAtUtc { get; set; }

	public virtual SyncMembershipEntity Membership { get; set; } = null!;
	public virtual MessageEntity Message { get; set; } = null!;
}
