using System.Security.Claims;
using Services.History.Models;
using Services.Utilities;

namespace Services.History.Interface;

/// <summary>
/// Reads the history one account context may actually see in a chat, and owns the private per-device
/// record of an archive having been downloaded (plan/08-history-archives.md).
///
/// It is the only place that resolves "current state vs frozen snapshot": a message inside a window of
/// an ACTIVE stint - including a newly shared period that overlaps an older archive - is answered with
/// today's statuses, while everything the caller only holds through an ended stint is answered from
/// that stint's own snapshot ("Новосподеленият период получава текущото разрешено състояние,
/// включително при припокриване. Старият архив извън новосподеления период остава замразен").
///
/// Authorization is never decided here: every answer goes through <see cref="Services.Membership.Interface.IChatAccessAuthority"/>,
/// the same check HTTP, socket and deferred delivery already share, which also folds in the stage-06
/// fresh-start exclusion.
///
/// This surface is deliberately legacy-shaped (legacy message ids and plaintext legacy content, keyset
/// paging by message id). It exists because messages reach a device only over the live socket today, so
/// without it neither newly shared history nor an archive on a newly authorized device could ever be
/// delivered. The stage-13 sync protocol (SyncPageRequest/cursors over the sync message stream)
/// replaces it.
/// </summary>
public interface IChatHistoryService
{
	/// <summary>
	/// One bounded page of permitted history, newest first. <paramref name="beforeMessageId"/> is the
	/// previous page's <see cref="ChatHistoryPage.NextBeforeMessageId"/>; null starts at the newest.
	/// With <paramref name="missingOnThisDeviceOnly"/> the page skips archived messages this SQLite copy
	/// has already confirmed storing, which is what lets an interrupted archive restore resume instead
	/// of starting over.
	/// </summary>
	Task<Result<ChatHistoryPage>> ReadAsync(
		ClaimsPrincipal caller, int legacyChatId, int? beforeMessageId, int limit, bool missingOnThisDeviceOnly, CancellationToken ct = default);

	/// <summary>
	/// The technical confirmation that this device stored archived messages: it updates the PRIVATE
	/// per-device record only. "Публичните отметки и unread в замразения чат остават непроменени" - no
	/// public status row is written, nothing is broadcast, and no other participant's view changes.
	/// Idempotent, so a lost response is retried safely; ids the caller may not read, or that are not in
	/// a frozen window, are ignored rather than refused (a mixed page is the normal case after a rejoin).
	/// </summary>
	Task<Result<ArchiveStoreResult>> ConfirmArchiveStoredAsync(
		ClaimsPrincipal caller, int legacyChatId, IReadOnlyCollection<int> legacyMessageIds, CancellationToken ct = default);
}
