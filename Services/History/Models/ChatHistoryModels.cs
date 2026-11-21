using Database.Enums;

namespace Services.History.Models;

/// <summary>
/// One permitted message with the statuses that apply to the caller's own view of it
/// (plan/08-history-archives.md). Nothing is returned for an instant the caller holds no window for:
/// "Недостъпните междини не получават съдържание или разделители в интерфейса" - a gap is simply
/// absent, never a placeholder.
/// </summary>
/// <param name="IsFrozen">This message falls in a frozen archive window, so its statuses are the ones captured at that stint's exit, not today's.</param>
/// <param name="StoredOnThisDevice">This exact SQLite copy has already confirmed storing it (private per-device progress; never part of any public mark).</param>
public sealed record ChatHistoryMessage(
	int MessageId,
	int SenderId,
	string Content,
	DateTime CreatedAt,
	bool IsFrozen,
	bool StoredOnThisDevice,
	IReadOnlyList<ChatHistoryStatus> Statuses);

/// <param name="Status">The highest status this recipient reached: <see cref="MessageStatus.Seen"/> implies it was received too.</param>
public sealed record ChatHistoryStatus(int ReceiverId, MessageStatus Status, DateTime Timestamp);

/// <param name="IsArchived">The caller holds this chat only as a read-only archive.</param>
/// <param name="NextBeforeMessageId">
/// Pass back as <c>beforeMessageId</c> to continue; null means the caller has reached the beginning of
/// what it may read. A page may be SMALLER than the limit - permission is evaluated per message - but
/// it always advances or terminates, so the restore loop makes progress either way.
/// </param>
public sealed record ChatHistoryPage(
	int ChatId,
	bool IsArchived,
	IReadOnlyList<ChatHistoryMessage> Messages,
	int? NextBeforeMessageId);

/// <summary>How many of the supplied messages this device's private archive progress now covers.</summary>
public sealed record ArchiveStoreResult(int ChatId, int Recorded, int AlreadyRecorded, int Ignored);
