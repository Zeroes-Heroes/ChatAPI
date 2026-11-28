using System.Security.Claims;
using Database.Entities;
using Database.Entities.Sync;
using Services.Membership.Models;
using Services.Statuses.Models;
using Services.Utilities;

namespace Services.Statuses.Interface;

/// <summary>
/// The ONE owner of what a message's status means (plan/10-recipient-status.md). Three things that
/// used to be the same thing are kept apart here:
/// <list type="bullet">
/// <item><b>device progress</b> - per SQLite copy: ciphertext stored, readable/received, really
/// observed. Written only from a device's own receipt, never inferred from presence or from the server
/// having sent the message.</item>
/// <item><b>the person's logical state</b> - per membership stint: Received once ANY of that person's
/// devices proved it, and Seen with the reason that first produced it (Observed, HistoryShared or
/// OwnSend). The first accepted Seen transition fixes reason and time for good.</item>
/// <item><b>unread</b> - per membership stint, decremented exactly once per message by that first
/// logical Seen.</item>
/// </list>
/// The server ACK (<c>new-message-created</c>) stays what it always was: proof the server stored the
/// message, and nothing about any recipient.
///
/// Every write here goes through <see cref="Services.Membership.Interface.IChatAccessAuthority"/>
/// (rights, fresh-start boundary, per-message windows) and - for the paths that own their transaction -
/// through <see cref="Services.Sessions.Service.TrustedMutation"/>, taking the chat's counter row
/// before any status row, which is the same order every membership mutation uses.
///
/// Everything the legacy hub produces is expressed over the LEGACY <c>Messages</c> table - the rows are
/// <c>SyncLegacyMessageRecipientStates</c> / <c>SyncLegacyMessageDeviceStates</c> and the legacy
/// <c>MessagesStatus</c> log is kept as a projection for the surfaces that still read it. Stage 11 adds
/// the first message in the SEQUENCE space (<c>SyncMessages</c>), whose unread and OwnSend marks live on
/// <c>SyncMessageRecipientStates</c> / <c>SyncUnreadStates</c>' sequence pointers; receipts over v1
/// messages (the device-state half of that space) are <see cref="AcceptSyncReceiptAsync"/> below.
///
/// A person's PUBLIC mark is stored per stint, so one person can hold more than one row for one message
/// after a rejoin. <see cref="Services.Statuses.Models.PublicRecipientState"/> is the one fold that
/// turns those rows into the single fact every reader shows.
/// </summary>
public interface IMessageStatusService
{
	/// <summary>
	/// ONE authenticated v1 receipt about ONE message in the SEQUENCE space, singular on the wire
	/// (<c>sync-receipt</c> takes one <c>ReceiptRequest</c>, not a batch).
	///
	/// Only <see cref="Sync.Contracts.ReceiptKind.CiphertextStored"/> and
	/// <see cref="Sync.Contracts.ReceiptKind.Received"/> are accepted; Seen is refused with
	/// <see cref="Sync.Contracts.SyncErrorCode.InvalidRequest"/> because fixing a logical Seen time,
	/// reason and unread decrement needs the clock rules and the client-reported-time column that belong
	/// to the later parts - a half-supported Seen would durably record a time they could not correct.
	///
	/// The scope's membership is the NAMED stint and is authorized against that stint's own message
	/// sequence grants, so a frozen archive is a legitimate scope (it updates private device progress
	/// only) and a rejoin's overlapping period is unambiguous. A permanently irrelevant receipt - no
	/// readable chat, no such stint of this account, no such message, or a message outside the named
	/// stint's grants - answers <see cref="Sync.Contracts.ReceiptDisposition.NotApplicable"/>, which is
	/// terminal. An authorization or infrastructure failure answers a
	/// <see cref="Sync.Contracts.SyncFailure"/> instead, so the device keeps the receipt queued.
	/// </summary>
	Task<Sync.Models.SyncResult<ReceiptAcceptance>> AcceptSyncReceiptAsync(
		ClaimsPrincipal caller, Sync.Contracts.ReceiptRequest request, CancellationToken ct = default);

	/// <summary>
	/// Accepts one bounded batch of this device's receipts for one chat. Idempotent per
	/// (message, device) and per (message, person): a retry after a lost response answers
	/// <see cref="Sync.Contracts.ReceiptDisposition.AlreadyApplied"/> with the same StateVersion, never
	/// a second unread decrement and never a moved Seen time. A message this caller holds no window for
	/// is <see cref="Sync.Contracts.ReceiptDisposition.NotApplicable"/> - terminal, so the device's queue
	/// can drop it instead of retrying forever; a message held only through a FROZEN window updates the
	/// private device row alone ("Публичните отметки и unread в замразения чат остават непроменени").
	/// </summary>
	Task<Result<ReceiptBatchResult>> AcceptReceiptsAsync(
		ClaimsPrincipal caller, int legacyChatId, IReadOnlyCollection<MessageReceipt> receipts, CancellationToken ct = default);

	/// <summary>
	/// The status half of accepting a new message, called INSIDE the sender's own write transaction
	/// (which already holds the chat row): it increments every eligible recipient's unread and marks the
	/// sender's own older incoming messages logically Seen with reason OwnSend. It opens no transaction,
	/// broadcasts nothing and leaves the message/key rows alone - the caller publishes the returned
	/// marks after its commit.
	/// </summary>
	/// <exception cref="InvalidOperationException">No explicit transaction is open on the shared DbContext.</exception>
	Task<NewMessageStatusEffects> RecordNewMessageAsync(
		ClaimsPrincipal sender, ChatAccess senderAccess, int legacyChatId, MessageEntity message, CancellationToken ct = default);

	/// <summary>
	/// The same status half for a v1 (sequence-space) message, called INSIDE the acceptance transaction
	/// of <see cref="Services.Messaging.Interface.IMessageAcceptanceService"/>, which already holds the
	/// chat row and has already allocated <paramref name="message"/>'s sequences. It raises every eligible
	/// recipient's unread and marks the sender's own older incoming messages Seen with reason OwnSend.
	///
	/// The OwnSend boundary is <paramref name="message"/>'s own <c>MessageSequence</c> - which is exactly
	/// what the wire contract returns as <c>OwnSendThroughSequence</c> - so a retry that returns the
	/// original accepted result returns the original boundary with it, and a later incoming message stays
	/// unread. Both message spaces are marked: a chat may hold legacy rows written before this stage and
	/// v1 rows written after it, and the person's "I have read everything older than my own send" is one
	/// fact, not two.
	/// </summary>
	/// <exception cref="InvalidOperationException">No explicit transaction is open on the shared DbContext.</exception>
	Task<NewMessageStatusEffects> RecordNewSyncMessageAsync(
		ClaimsPrincipal sender, ChatAccess senderAccess, int legacyChatId, SyncMessageEntity message, CancellationToken ct = default);

	/// <summary>
	/// The caller has the chat open: every permitted message it has not logically Seen becomes Seen with
	/// reason Observed, its unread goes to what is left, and this device's own observation is recorded
	/// separately. Owns its transaction and its trust re-check; returns the marks to publish.
	///
	/// Per-message visibility (50 % for 500 ms, invisible mode, the covering-screen rule) is stage 17 and
	/// is deliberately NOT claimed here - this is the same coarse "the chat is open" signal the legacy
	/// hub always used, now recorded in the logical model with a reason.
	/// </summary>
	Task<Result<ReceiptBatchResult>> MarkChatObservedAsync(ClaimsPrincipal caller, int legacyChatId, CancellationToken ct = default);

	/// <summary>
	/// Marks the older messages a membership change just shared logically Seen for the added member,
	/// with reason HistoryShared and no unread ("Споделените стари съобщения се маркират логически Seen
	/// за новия участник, без unread и известия за ново входящо"). Called INSIDE the membership
	/// mutation's own transaction, under the chat row it already holds.
	/// </summary>
	/// <exception cref="InvalidOperationException">No explicit transaction is open on the shared DbContext.</exception>
	Task<IReadOnlyList<RecipientStatusMark>> MarkHistorySharedSeenAsync(
		string membershipId, string accountId, int legacyChatId, int targetLegacyUserId, DateTime atUtc,
		IReadOnlyCollection<int> legacyMessageIds, CancellationToken ct = default);

	/// <summary>
	/// This caller's unread count per chat, from the authoritative per-stint counter. A chat the server
	/// keeps no counter for - no active stint, or none that has ever been incremented - is simply ABSENT
	/// from the answer rather than present as 0, so a surface can tell "the server says zero" from "the
	/// server has nothing to say" and leave the client's own count alone (see
	/// <c>GetChatsResponse.UnreadCount</c>). A rejoin answers its NEW stint's counter, so the old
	/// archive's unread is structurally outside it.
	/// </summary>
	Task<IReadOnlyDictionary<int, long>> UnreadCountsAsync(
		ClaimsPrincipal caller, IReadOnlyCollection<int> legacyChatIds, CancellationToken ct = default);

	/// <summary>
	/// The status view of messages the caller may read: who has the right to each one (author excluded)
	/// and each of those people's logical state. <paramref name="legacyMessageIds"/> must already be
	/// authorized by the caller's own windows - this answers about them, it does not grant them.
	/// </summary>
	Task<IReadOnlyDictionary<int, MessageStatusView>> DescribeAsync(
		int legacyChatId, IReadOnlyCollection<int> legacyMessageIds, CancellationToken ct = default);
}
