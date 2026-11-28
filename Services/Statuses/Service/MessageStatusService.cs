using System.Net;
using System.Security.Claims;
using Database.Context;
using Database.Entities;
using Database.Entities.Sync;
using Database.Enums;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Services.Delivery;
using Services.Extensions;
using Services.Membership;
using Services.Membership.Interface;
using Services.Membership.Models;
using Services.Sessions;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Services.Statuses.Interface;
using Services.Statuses.Models;
using Services.Sync.Contracts;
using Services.Utilities;

namespace Services.Statuses.Service;

/// <summary>
/// See <see cref="IMessageStatusService"/>. Everything public this writes happens under the chat's own
/// counter row (taken through <see cref="IChatAccessAuthority.LockChatAndAuthorizeAsync"/>), so the
/// order is the established account -&gt; device -&gt; session -&gt; chat -&gt; status/unread rows: two
/// devices of one person, a receipt and a concurrent removal, and a send and a receipt all serialize at
/// exactly one point, in either arrival order, and the FIRST accepted Seen transition is the one that
/// fixes reason/time and decrements unread.
/// </summary>
public sealed partial class MessageStatusService(
	AppDbContext db, IChatAccessAuthority accessAuthority, ISessionTrustChecker trustChecker,
	IOptions<DeliveryOptions> delivery, TimeProvider timeProvider)
	: IMessageStatusService
{
	/// <summary>docs/sync-contract-v1.md: "Receipt batches cap at both 50 and 32768 bytes." The byte cap is the hub's own transport limit.</summary>
	public const int MaxReceiptBatch = 50;

	public async Task<Result<ReceiptBatchResult>> AcceptReceiptsAsync(
		ClaimsPrincipal caller, int legacyChatId, IReadOnlyCollection<MessageReceipt> receipts, CancellationToken ct = default)
	{
		if (receipts.Count == 0)
		{
			return Result<ReceiptBatchResult>.Success(new ReceiptBatchResult(legacyChatId, [], []));
		}

		if (receipts.Count > MaxReceiptBatch)
		{
			return Result<ReceiptBatchResult>.Failure(
				$"At most {MaxReceiptBatch} receipts can be reported in one request.", HttpStatusCode.BadRequest);
		}

		// "Only Seen carries Observation; other receipt kinds require null" - a malformed batch is
		// refused as a whole rather than silently half-applied.
		if (receipts.Any(r => r.Observation is not null && r.Kind != ReceiptKind.Seen))
		{
			return Result<ReceiptBatchResult>.Failure("Only a Seen receipt may carry an observation.", HttpStatusCode.BadRequest);
		}

		if (await DeviceOfAsync(caller, ct) is not DeviceIdentity device)
		{
			return Result<ReceiptBatchResult>.Failure("This session has no local instance.", HttpStatusCode.BadRequest);
		}

		// Last one wins per message, so a batch that reports both CiphertextStored and Received for the
		// same message is still one decision per message - and the loop below is monotonic anyway.
		MessageReceipt[] ordered = [.. receipts.OrderBy(r => r.LegacyMessageId).ThenBy(r => (int)r.Kind)];

		return await WriteAsync(caller, legacyChatId, device, async (access, now) =>
		{
			List<MessageReceiptOutcome> outcomes = [];
			List<AppliedMark> applied = [];

			Dictionary<int, MessageEntity> messages = await MessagesOfAsync(
				legacyChatId, [.. ordered.Select(r => r.LegacyMessageId).Distinct()], ct);

			foreach (MessageReceipt receipt in ordered)
			{
				outcomes.Add(await ApplyReceiptAsync(
					caller, access, device, legacyChatId, messages, receipt, now, applied, deviceHoldsIt: true, ct));
			}

			return (outcomes, applied);
		}, ct);
	}

	public async Task<Result<ReceiptBatchResult>> MarkChatObservedAsync(
		ClaimsPrincipal caller, int legacyChatId, CancellationToken ct = default)
	{
		if (await DeviceOfAsync(caller, ct) is not DeviceIdentity device)
		{
			return Result<ReceiptBatchResult>.Failure("This session has no local instance.", HttpStatusCode.BadRequest);
		}

		return await WriteAsync(caller, legacyChatId, device, async (access, now) =>
		{
			if (!access.CanSend)
			{
				// A read-only archive publishes nothing: entering it must not mark its messages Seen for
				// everyone else ("Публичните отметки и unread в замразения чат остават непроменени").
				return ([], []);
			}

			List<MessageReceiptOutcome> outcomes = [];
			List<AppliedMark> applied = [];

			// Everything this person may currently read and has not logically Seen yet. The coarse "the
			// chat is open" signal of the legacy hub, now recorded with a reason - per-message visibility
			// is stage 17.
			foreach (MessageEntity message in await UnseenInCurrentWindowsAsync(
				access, legacyChatId, caller.Id(), device.AccountContextId, ct))
			{
				MessageReceipt receipt = new(message.Id, ReceiptKind.Seen);
				outcomes.Add(await ApplyReceiptAsync(
					caller, access, device, legacyChatId, new Dictionary<int, MessageEntity> { [message.Id] = message },
					receipt, now, applied, deviceHoldsIt: false, ct));
			}

			return (outcomes, applied);
		}, ct);
	}

	public async Task<NewMessageStatusEffects> RecordNewMessageAsync(
		ClaimsPrincipal sender, ChatAccess senderAccess, int legacyChatId, MessageEntity message, CancellationToken ct = default)
	{
		RequireTransaction(nameof(RecordNewMessageAsync));

		int senderLegacyUserId = sender.Id();
		IReadOnlyList<ChatParticipantAccess> participants = await accessAuthority.DescribeParticipantsAsync(legacyChatId, ct);

		// "Получателите се броят по хора, като подателят е изключен" - everyone whose own windows permit
		// this exact message, author excluded. A member added later is not in it; a removed member whose
		// archive covers the message still is.
		ChatParticipantAccess[] recipients = [.. participants
			.Where(p => p.LegacyUserId != senderLegacyUserId && p.Access.PermitsMessageAt(message.CreatedAt))];

		// Counted per PERSON, exactly as GET /chat/history counts it: one unread counter is raised per
		// active stint, but a person that somehow holds two of them is still one recipient, and the two
		// surfaces must never disagree about the same message's denominator.
		int recipientCount = recipients.Select(p => p.LegacyUserId).Distinct().Count();

		foreach (ChatParticipantAccess recipient in recipients
			.Where(p => p.AccountContextId is not null)
			// Ascending membership id: several unread rows are taken in one order by every writer, so a
			// send to a group and a concurrent receipt can never build a cycle.
			.OrderBy(p => ActiveStintOf(p.Access), StringComparer.Ordinal))
		{
			if (ActiveStintOf(recipient.Access) is not string stintId)
			{
				// Only an active stint has a current unread counter: a frozen archive's unread is its own
				// stint's and must not be touched by new content it will never receive.
				continue;
			}

			await IncrementUnreadAsync(stintId, SyncChatIdentity.IdOf(legacyChatId), message, ct);
		}

		// "При първия успешен commit на собствено ново съобщение по-старите входящи в текущия разрешен
		// обхват на чата се маркират логически прочетени за подателя." The boundary is this message's own
		// creation instant, so later incoming messages stay unread.
		IReadOnlyList<RecipientStatusMark> ownSend = await MarkOwnSendSeenAsync(
			sender, senderAccess, legacyChatId, message.CreatedAt, syncBoundary: null, markedSyncMessageIds: null, ct);

		return new NewMessageStatusEffects(recipientCount, ownSend);
	}

	public async Task<NewMessageStatusEffects> RecordNewSyncMessageAsync(
		ClaimsPrincipal sender, ChatAccess senderAccess, int legacyChatId, SyncMessageEntity message, CancellationToken ct = default)
	{
		RequireTransaction(nameof(RecordNewSyncMessageAsync));

		int senderLegacyUserId = sender.Id();
		IReadOnlyList<ChatParticipantAccess> participants = await accessAuthority.DescribeParticipantsAsync(legacyChatId, ct);

		// Identical rule to the legacy path: everyone whose own windows permit this exact message, author
		// excluded, counted per PERSON. The windows are evaluated in legacy TIME because that is the only
		// boundary a stint carries today; a v1 message's CreatedAtUtc is on the same clock.
		ChatParticipantAccess[] recipients = [.. participants
			.Where(p => p.LegacyUserId != senderLegacyUserId && p.Access.PermitsMessageAt(message.CreatedAtUtc))];

		int recipientCount = recipients.Select(p => p.LegacyUserId).Distinct().Count();
		List<string> unreadChanged = [];

		foreach (ChatParticipantAccess recipient in recipients
			.Where(p => p.AccountContextId is not null)
			// Ascending membership id, the one order every writer here takes several unread rows in.
			.OrderBy(p => ActiveStintOf(p.Access), StringComparer.Ordinal))
		{
			if (ActiveStintOf(recipient.Access) is not string stintId)
			{
				continue;
			}

			SyncUnreadStateEntity state = await UnreadStateAsync(stintId, message.ChatId, ct);
			state.Count++;
			state.OldestUnreadMessageId ??= message.Id;
			state.LatestUnreadMessageId = message.Id;
			state.LatestUnreadCreatedAtUtc = message.CreatedAtUtc;
			state.Version++;
			unreadChanged.Add(stintId);
		}

		// One logical effect in both spaces, and one unread recomputation covering both: the legacy marks
		// use this message's creation instant (legacy rows carry no sequence), the v1 marks its own
		// MessageSequence.
		List<string> ownSendSyncMessageIds = [];
		IReadOnlyList<RecipientStatusMark> ownSend = await MarkOwnSendSeenAsync(
			sender, senderAccess, legacyChatId, message.CreatedAtUtc, message, ownSendSyncMessageIds, ct);

		if ((ownSendSyncMessageIds.Count > 0 || ownSend.Count > 0) && ActiveStintOf(senderAccess) is string senderStintId)
		{
			// Either kind of mark recomputes the sender's own counter (a stint has ONE counter covering
			// both message spaces), so it owes a journal entry exactly like the recipients'.
			unreadChanged.Add(senderStintId);
		}

		return new NewMessageStatusEffects(recipientCount, ownSend, unreadChanged, ownSendSyncMessageIds);
	}

	/// <summary>
	/// The sender's own older v1 incoming messages, strictly BELOW this message's own sequence, inside
	/// its current (non-frozen) windows. Produces nothing to publish: live delivery of a v1 status change
	/// is the journal's and stage 12's business, not the legacy status event's.
	/// </summary>
	private async Task MarkOwnSendSeenInSequenceSpaceAsync(
		string stintId, string accountId, ChatAccess access, SyncMessageEntity message, List<string> markedMessageIds, CancellationToken ct)
	{
		foreach (SyncMessageEntity earlier in await UnseenSyncInCurrentWindowsAsync(access, message.ChatId, accountId, stintId, ct))
		{
			// "Повторението на операцията запазва същата граница; по-късните входящи остават непрочетени."
			if (earlier.MessageSequence >= message.MessageSequence)
			{
				continue;
			}

			SyncMessageRecipientStateEntity state = await SyncRecipientStateAsync(stintId, accountId, message.ChatId, earlier.Id, ct);
			if (state.SeenAtUtc is not null)
			{
				continue;
			}

			state.SeenAtUtc = message.CreatedAtUtc;
			state.SeenReason = SyncSeenReason.OwnSend;
			// Seen implies the person has the message - the same monotonicity the legacy rows keep.
			state.ReceivedAtUtc ??= message.CreatedAtUtc;
			state.Version++;
			markedMessageIds.Add(earlier.Id);
		}
	}

	/// <summary>
	/// The sequence-space counterpart of <see cref="UnseenInCurrentWindowsAsync"/>: v1 messages of this
	/// chat written by somebody else, inside this stint's still-open windows, with no logical Seen yet.
	/// </summary>
	private async Task<IReadOnlyList<SyncMessageEntity>> UnseenSyncInCurrentWindowsAsync(
		ChatAccess access, string chatId, string accountId, string stintId, CancellationToken ct)
	{
		LegacyHistoryWindow[] current = [.. access.Windows.Where(w => !w.IsFrozen)];
		if (current.Length == 0)
		{
			return [];
		}

		DateTime? from = current.Any(w => w.FromUtc is null) ? null : current.Min(w => w.FromUtc);
		DateTime? through = current.Any(w => w.ThroughUtc is null) ? null : current.Max(w => w.ThroughUtc);

		List<SyncMessageEntity> candidates = await db.SyncMessages.AsNoTracking()
			.Where(m => m.ChatId == chatId
				&& m.SenderAccountId != accountId
				&& (from == null || m.CreatedAtUtc >= from)
				&& (through == null || m.CreatedAtUtc <= through)
				&& !db.SyncMessageRecipientStates.Any(s => s.MessageId == m.Id && s.MembershipId == stintId && s.SeenAtUtc != null))
			.OrderBy(m => m.MessageSequence)
			.ToListAsync(ct);

		// The exact per-message rule after the bounded scan: a gap between two windows is covered by no
		// window at all.
		return [.. candidates.Where(m => current.Any(w => w.Contains(m.CreatedAtUtc)))];
	}

	private async Task<SyncMessageRecipientStateEntity> SyncRecipientStateAsync(
		string membershipId, string accountId, string chatId, string messageId, CancellationToken ct)
	{
		SyncMessageRecipientStateEntity? state = db.SyncMessageRecipientStates.Local
			.FirstOrDefault(s => s.MembershipId == membershipId && s.MessageId == messageId)
			?? await db.SyncMessageRecipientStates
				.FirstOrDefaultAsync(s => s.MembershipId == membershipId && s.MessageId == messageId, ct);
		if (state is not null)
		{
			return state;
		}

		state = new SyncMessageRecipientStateEntity(messageId, chatId, membershipId, accountId, version: 0);
		db.SyncMessageRecipientStates.Add(state);
		return state;
	}

	public async Task<IReadOnlyList<RecipientStatusMark>> MarkHistorySharedSeenAsync(
		string membershipId, string accountId, int legacyChatId, int targetLegacyUserId, DateTime atUtc,
		IReadOnlyCollection<int> legacyMessageIds, CancellationToken ct = default)
	{
		RequireTransaction(nameof(MarkHistorySharedSeenAsync));

		if (legacyMessageIds.Count == 0)
		{
			return [];
		}

		Dictionary<int, MessageEntity> messages = await MessagesOfAsync(legacyChatId, [.. legacyMessageIds.Distinct()], ct);
		List<AppliedMark> applied = [];

		foreach (MessageEntity message in messages.Values.OrderBy(m => m.Id))
		{
			if (message.SenderId == targetLegacyUserId)
			{
				// A member is never its own recipient, not even for history it was just given.
				continue;
			}

			SyncLegacyMessageRecipientStateEntity state = await RecipientStateAsync(
				membershipId, accountId, SyncChatIdentity.IdOf(legacyChatId), message.Id, ct);

			// An existing Seen - from an earlier stint whose archive overlaps this period - is left
			// exactly as it is: the first accepted transition owns the time and the reason for good.
			if (state.SeenAtUtc is not null)
			{
				continue;
			}

			ApplySeen(state, SyncSeenReason.HistoryShared, atUtc, clientReportedAtUtc: null);

			// Reported to the author only when the PUBLIC state actually changed. On a rejoin whose new
			// period overlaps an older archive the person was already marked Seen once; the new stint gets
			// its own logical row, but nobody is told a second time about a status that never moved.
			if (await ProjectToLegacyLogAsync(message.Id, targetLegacyUserId, MessageStatus.Seen, atUtc, ct))
			{
				applied.Add(new AppliedMark(message.SenderId, targetLegacyUserId, IsSeen: true, atUtc, message.Id));
			}
		}

		// Deliberately no unread change: "Споделените стари съобщения се маркират логически Seen за
		// новия участник, без unread и известия за ново входящо" - the new stint's counter starts at zero
		// and this marking must not move it in either direction.
		return Group(applied);
	}

	public async Task<IReadOnlyDictionary<int, long>> UnreadCountsAsync(
		ClaimsPrincipal caller, IReadOnlyCollection<int> legacyChatIds, CancellationToken ct = default)
	{
		Dictionary<int, long> result = [];
		if (legacyChatIds.Count == 0)
		{
			return result;
		}

		string? accountId = caller.FindFirstValue(SessionClaimTypes.AccountContextId);
		if (string.IsNullOrEmpty(accountId))
		{
			return result;
		}

		string[] chatIds = [.. legacyChatIds.Select(SyncChatIdentity.IdOf)];

		// Only the CURRENT (active) stint's counter: a frozen archive's unread is its own stint's row and
		// is excluded from the current state ("Непрочетеното само в замразения архив след rejoin се
		// изключва от текущия unread и sync приоритета").
		var counters = await db.SyncUnreadStates.AsNoTracking()
			.Where(u => chatIds.Contains(u.ChatId)
				&& u.Membership.AccountId == accountId
				&& u.Membership.State == SyncMembershipState.Active)
			.Select(u => new { u.ChatId, u.Count })
			.ToListAsync(ct);

		foreach (var counter in counters)
		{
			if (int.TryParse(counter.ChatId, out int legacyChatId))
			{
				result[legacyChatId] = counter.Count;
			}
		}

		return result;
	}

	public async Task<IReadOnlyDictionary<int, MessageStatusView>> DescribeAsync(
		int legacyChatId, IReadOnlyCollection<int> legacyMessageIds, CancellationToken ct = default)
	{
		Dictionary<int, MessageStatusView> result = [];
		if (legacyMessageIds.Count == 0)
		{
			return result;
		}

		int[] ids = [.. legacyMessageIds.Distinct()];
		var messages = await db.Messages.AsNoTracking()
			.Where(m => m.ChatId == legacyChatId && ids.Contains(m.Id))
			.Select(m => new { m.Id, m.SenderId, m.CreatedAt })
			.ToListAsync(ct);

		if (messages.Count == 0)
		{
			return result;
		}

		IReadOnlyList<ChatParticipantAccess> participants = await accessAuthority.DescribeParticipantsAsync(legacyChatId, ct);
		string chatId = SyncChatIdentity.IdOf(legacyChatId);

		List<SyncLegacyMessageRecipientStateEntity> states = await db.SyncLegacyMessageRecipientStates.AsNoTracking()
			.Where(s => s.ChatId == chatId && ids.Contains(s.LegacyMessageId))
			.ToListAsync(ct);

		Dictionary<string, int> legacyUserOf = participants
			.Where(p => p.AccountContextId is not null)
			.ToDictionary(p => p.AccountContextId!, p => p.LegacyUserId, StringComparer.Ordinal);

		// The legacy log still answers for everything that happened before this stage; a logical row, once
		// it exists, is the authority for that person and message.
		List<MessageStatusEntity> legacy = await db.MessagesStatus.AsNoTracking()
			.Where(s => ids.Contains(s.MessageId))
			.ToListAsync(ct);

		foreach (var message in messages)
		{
			int[] entitled = [.. participants
				.Where(p => p.LegacyUserId != message.SenderId && p.Access.PermitsMessageAt(message.CreatedAt))
				.Select(p => p.LegacyUserId)
				.Distinct()];

			List<RecipientStatus> statuses = [];
			foreach (int recipient in entitled)
			{
				// A person who rejoined has one row per stint for the same message (the old archive's and
				// the new period's). What is shown is the furthest state that person ever reached - a
				// status must not go backwards because a newer stint has not repeated an older mark.
				SyncLegacyMessageRecipientStateEntity? state = states
					.Where(s => s.LegacyMessageId == message.Id
						&& legacyUserOf.TryGetValue(s.AccountId, out int legacyUserId)
						&& legacyUserId == recipient)
					.OrderByDescending(s => s.SeenAtUtc is not null)
					.ThenByDescending(s => s.ReceivedAtUtc is not null)
					.ThenByDescending(s => s.Version)
					.FirstOrDefault();

				if (state is not null)
				{
					statuses.Add(new RecipientStatus(recipient, state.ReceivedAtUtc, state.SeenAtUtc, Reason(state.SeenReason)));
					continue;
				}

				MessageStatusEntity[] rows = [.. legacy.Where(s => s.MessageId == message.Id && s.ReceiverId == recipient)];
				if (rows.Length == 0)
				{
					continue;
				}

				MessageStatusEntity? seen = rows.FirstOrDefault(s => s.Status == MessageStatus.Seen);
				MessageStatusEntity? received = rows.FirstOrDefault(s => s.Status == MessageStatus.Received);
				// A pre-stage-10 row carries no reason at all, and one is not invented here: the legacy
				// log never recorded WHY something was seen.
				statuses.Add(new RecipientStatus(recipient, received?.Timestamp, seen?.Timestamp, SeenReason: null));
			}

			result[message.Id] = new MessageStatusView(message.Id, entitled.Length, statuses);
		}

		return result;
	}

	/// <summary>
	/// The one write shape both device-driven paths share: this caller's own trusted transaction, the
	/// chat row, current rights re-read under it, then the per-message decisions, then unread, then the
	/// commit. Nothing is broadcast here - the marks travel back to the caller, which publishes after the
	/// commit, so a crash in between loses a live event and never a status.
	/// </summary>
	private async Task<Result<ReceiptBatchResult>> WriteAsync(
		ClaimsPrincipal caller, int legacyChatId, DeviceIdentity device,
		Func<ChatAccess, DateTime, Task<(List<MessageReceiptOutcome> Outcomes, List<AppliedMark> Applied)>> apply,
		CancellationToken ct)
	{
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;
		Result<ReceiptBatchResult>? refusal = null;
		List<MessageReceiptOutcome> outcomes = [];
		List<AppliedMark> applied = [];

		TrustCheckResult trust = await TrustedMutation.RunAsync(db, trustChecker, caller, async () =>
		{
			ChatAccess access = await accessAuthority.LockChatAndAuthorizeAsync(caller, legacyChatId, ct);
			if (!access.CanRead)
			{
				// Indistinguishable from a chat that does not exist - an authorization failure must not
				// confirm someone else's chat.
				refusal = Result<ReceiptBatchResult>.Failure("Chat not found.", HttpStatusCode.NotFound);
				return;
			}

			(outcomes, applied) = await apply(access, now);

			if (applied.Any(mark => mark.IsSeen))
			{
				// Flushed first, inside the same transaction: the recomputation below asks the DATABASE
				// what is still unread, and the rows this operation just produced have to be part of that
				// answer rather than sitting unsaved in the change tracker.
				await db.SaveChangesAsync(ct);

				// One recomputation per operation rather than per message: the counter is the number of
				// permitted, not-yet-logically-seen incoming messages of this stint, which is exact by
				// construction - a repeated receipt can never decrement it twice.
				await RecomputeUnreadAsync(access, legacyChatId, caller.Id(), device.AccountContextId, ct);
			}

			await db.SaveChangesAsync(ct);
		}, ct);

		if (!trust.IsTrusted)
		{
			return Result<ReceiptBatchResult>.Failure("Session is not currently trusted.", HttpStatusCode.Unauthorized);
		}

		return refusal ?? Result<ReceiptBatchResult>.Success(
			new ReceiptBatchResult(legacyChatId, outcomes, Group(applied)));
	}

	/// <summary>
	/// One receipt. The device row is always this caller's own instance; a public mark happens only for a
	/// message of somebody else inside a window that is still open. The three kinds are deliberately not
	/// collapsed: stored ciphertext is not readiness, and readiness is not an observation.
	/// </summary>
	/// <param name="deviceHoldsIt">
	/// Whether this receipt is the device's own evidence about a copy it has (a real receipt), or a
	/// person-level signal that happens to arrive over this connection (entering the chat). In the second
	/// case an existing device row is still updated, but none is CREATED: a device that never stored the
	/// message must not end up claiming it observed it.
	/// </param>
	private async Task<MessageReceiptOutcome> ApplyReceiptAsync(
		ClaimsPrincipal caller, ChatAccess access, DeviceIdentity device, int legacyChatId,
		IReadOnlyDictionary<int, MessageEntity> messages, MessageReceipt receipt, DateTime now,
		List<AppliedMark> applied, bool deviceHoldsIt, CancellationToken ct)
	{
		if (!messages.TryGetValue(receipt.LegacyMessageId, out MessageEntity? message)
			|| access.WindowAt(message.CreatedAt) is not LegacyHistoryWindow window)
		{
			// "NotApplicable is terminal for stale public receipts" - an unknown message, a message of
			// another chat, or one this caller holds no window for. The device drops it instead of
			// retrying forever.
			return new MessageReceiptOutcome(receipt.LegacyMessageId, ReceiptDisposition.NotApplicable, null);
		}

		if (window.MembershipId is null)
		{
			// A chat with no membership projection (created before stage 07) has no stint to key a logical
			// row by. Its legacy marks keep working exactly as they did, so this is an honest degradation
			// rather than a silent loss: the public projection is written, nothing new-model is.
			return await ApplyLegacyOnlyAsync(caller, message, receipt, now, applied, ct);
		}

		SyncLegacyMessageDeviceStateEntity? deviceState = await DeviceStateAsync(
			device, window.MembershipId, legacyChatId, message.Id, create: deviceHoldsIt, ct);
		bool changed = deviceState is not null && ApplyDeviceState(deviceState, receipt.Kind, now);

		bool isOwnMessage = message.SenderId == caller.Id();
		if (isOwnMessage || window.IsFrozen || receipt.Kind == ReceiptKind.CiphertextStored)
		{
			// Private progress only. An author is not its own recipient (it is excluded from every
			// proportion), a frozen archive's public marks are fixed at the removal
			// ("Публичните отметки и unread в замразения чат остават непроменени"), and stored ciphertext
			// is explicitly not readiness ("Записването само на ciphertext се отчита отделно").
			return new MessageReceiptOutcome(
				message.Id,
				changed ? ReceiptDisposition.Applied : ReceiptDisposition.AlreadyApplied,
				deviceState?.Version);
		}

		SyncLegacyMessageRecipientStateEntity state = await RecipientStateAsync(
			window.MembershipId, device.AccountContextId, SyncChatIdentity.IdOf(legacyChatId), message.Id, ct);

		bool publicChange = receipt.Kind switch
		{
			ReceiptKind.Received => ApplyReceived(state, now),
			ReceiptKind.Seen => ApplyObservedSeen(state, now, receipt.Observation?.ClientObservedAtUtc),
			_ => false,
		};

		if (publicChange)
		{
			bool seen = receipt.Kind == ReceiptKind.Seen;
			await ProjectToLegacyLogAsync(message.Id, caller.Id(), seen ? MessageStatus.Seen : MessageStatus.Received, now, ct);
			applied.Add(new AppliedMark(message.SenderId, caller.Id(), seen, now, message.Id));
		}

		return new MessageReceiptOutcome(
			message.Id,
			changed || publicChange ? ReceiptDisposition.Applied : ReceiptDisposition.AlreadyApplied,
			state.Version);
	}

	/// <summary>
	/// The pre-stage-07 chat fallback: no stint exists, so the legacy log is both the projection and the
	/// whole state. It is applied with exactly the monotonicity of the projected path - the pair's rows
	/// are read first, a recorded Seen already implies the person has the message, and only a real
	/// transition publishes anything - so a retried or late receipt can neither duplicate a row nor move
	/// the author's view backwards.
	/// </summary>
	private async Task<MessageReceiptOutcome> ApplyLegacyOnlyAsync(
		ClaimsPrincipal caller, MessageEntity message, MessageReceipt receipt, DateTime now, List<AppliedMark> applied, CancellationToken ct)
	{
		if (receipt.Kind == ReceiptKind.CiphertextStored || message.SenderId == caller.Id())
		{
			return new MessageReceiptOutcome(message.Id, ReceiptDisposition.NotApplicable, null);
		}

		int receiverId = caller.Id();
		(bool received, bool seen) = await LegacyStateAsync(message.Id, receiverId, ct);
		bool reportsSeen = receipt.Kind == ReceiptKind.Seen;

		// A Received arriving after a Seen changes nothing: the projected path's ApplySeen back-fills
		// ReceivedAtUtc, so a recorded Seen already means this person has the message. Publishing the
		// Received would take the author's tick from Seen back to Delivered with nothing to restore it -
		// the exact regression the removed reconnect sweep used to guard against.
		if (reportsSeen ? seen : received || seen)
		{
			return new MessageReceiptOutcome(message.Id, ReceiptDisposition.AlreadyApplied, LegacyStateVersion(received, seen));
		}

		await ProjectToLegacyLogAsync(message.Id, receiverId, reportsSeen ? MessageStatus.Seen : MessageStatus.Received, now, ct);
		applied.Add(new AppliedMark(message.SenderId, receiverId, reportsSeen, now, message.Id));

		return new MessageReceiptOutcome(
			message.Id, ReceiptDisposition.Applied, LegacyStateVersion(received || !reportsSeen, seen || reportsSeen));
	}

	/// <summary>
	/// The whole state of one (message, person) pair in a chat with no membership projection: the legacy
	/// log's two possible rows. The change tracker is consulted as well - one batch can touch the same
	/// pair twice, and a row added a moment ago is not in the database yet.
	/// </summary>
	private async Task<(bool Received, bool Seen)> LegacyStateAsync(int legacyMessageId, int receiverLegacyUserId, CancellationToken ct)
	{
		List<MessageStatus> rows = [.. db.MessagesStatus.Local
			.Where(s => s.MessageId == legacyMessageId && s.ReceiverId == receiverLegacyUserId)
			.Select(s => s.Status)];

		rows.AddRange(await db.MessagesStatus.AsNoTracking()
			.Where(s => s.MessageId == legacyMessageId && s.ReceiverId == receiverLegacyUserId)
			.Select(s => s.Status)
			.ToListAsync(ct));

		return (rows.Contains(MessageStatus.Received), rows.Contains(MessageStatus.Seen));
	}

	/// <summary>
	/// docs/sync-contract-v1.md: "Applied/AlreadyApplied have StateVersion, NotApplicable has null." A
	/// chat with no stint has no versioned row, so the version is the number of logical transitions the
	/// legacy log records for the pair (nothing -&gt; received -&gt; seen). Monotonic by construction: the
	/// log is append-only and this path never appends a status below the one already recorded.
	/// </summary>
	private static long LegacyStateVersion(bool received, bool seen) => (received || seen ? 1 : 0) + (seen ? 1 : 0);

	/// <summary>Monotonic by construction: a timestamp that is already set is never moved, so a repeat is a no-op.</summary>
	private static bool ApplyDeviceState(SyncLegacyMessageDeviceStateEntity state, ReceiptKind kind, DateTime now)
	{
		bool changed = false;

		if (kind is ReceiptKind.CiphertextStored or ReceiptKind.Received && state.CiphertextStoredAtUtc is null)
		{
			// A readable copy implies the bytes are on disk, so Received backfills the earlier step rather
			// than leaving a device that only ever reported Received looking like it holds nothing.
			state.CiphertextStoredAtUtc = now;
			changed = true;
		}

		if (kind == ReceiptKind.Received && state.ReceivedAtUtc is null)
		{
			state.ReceivedAtUtc = now;
			changed = true;
		}

		if (kind == ReceiptKind.Seen && state.ObservedSeenAtUtc is null)
		{
			state.ObservedSeenAtUtc = now;
			changed = true;
		}

		if (changed)
		{
			state.Version++;
		}

		return changed;
	}

	private static bool ApplyReceived(SyncLegacyMessageRecipientStateEntity state, DateTime now)
	{
		// "На автора „Изпратено“ за даден получател се показва след Received от поне едно негово
		// устройство" - the first device that proves it owns the moment; later devices change nothing.
		if (state.ReceivedAtUtc is not null)
		{
			return false;
		}

		state.ReceivedAtUtc = now;
		state.Version++;
		return true;
	}

	private static bool ApplyObservedSeen(SyncLegacyMessageRecipientStateEntity state, DateTime now, DateTime? clientReportedAtUtc)
	{
		if (state.SeenAtUtc is not null)
		{
			// "Следващите device Seen записи запазват този общ час" - a second device's observation never
			// replaces the fixed one, and never decrements unread again.
			return false;
		}

		ApplySeen(state, SyncSeenReason.Observed, now, clientReportedAtUtc);
		return true;
	}

	private static void ApplySeen(
		SyncLegacyMessageRecipientStateEntity state, SyncSeenReason reason, DateTime atUtc, DateTime? clientReportedAtUtc)
	{
		state.SeenAtUtc = atUtc;
		state.SeenReason = reason;
		state.SeenClientReportedAtUtc = clientReportedAtUtc;
		// Seen implies the person has the message: a status that claims it was read but never received
		// would make the author's view go backwards.
		state.ReceivedAtUtc ??= atUtc;
		state.Version++;
	}

	/// <summary>
	/// The sender's own older incoming messages, inside its CURRENT windows only - "Замразеният архив
	/// извън новосподеления период запазва статусите си".
	/// </summary>
	/// <param name="syncBoundary">
	/// Set only for a v1 send: the accepted message, whose own <c>MessageSequence</c> is the boundary for
	/// the sequence-space half of the same marking. Both halves and the single unread recomputation that
	/// follows them stay in one place, because a stint has exactly one counter covering both spaces.
	/// </param>
	private async Task<IReadOnlyList<RecipientStatusMark>> MarkOwnSendSeenAsync(
		ClaimsPrincipal sender, ChatAccess access, int legacyChatId, DateTime boundaryUtc,
		SyncMessageEntity? syncBoundary, List<string>? markedSyncMessageIds, CancellationToken ct)
	{
		if (ActiveStintOf(access) is not string stintId
			|| sender.FindFirstValue(SessionClaimTypes.AccountContextId) is not string accountId)
		{
			return [];
		}

		int senderLegacyUserId = sender.Id();
		List<AppliedMark> applied = [];

		foreach (MessageEntity message in await UnseenInCurrentWindowsAsync(access, legacyChatId, senderLegacyUserId, accountId, ct))
		{
			if (message.CreatedAt >= boundaryUtc)
			{
				// "Повторението на операцията запазва същата граница; по-късните входящи остават
				// непрочетени."
				continue;
			}

			SyncLegacyMessageRecipientStateEntity state = await RecipientStateAsync(
				stintId, accountId, SyncChatIdentity.IdOf(legacyChatId), message.Id, ct);
			if (state.SeenAtUtc is not null)
			{
				continue;
			}

			ApplySeen(state, SyncSeenReason.OwnSend, boundaryUtc, clientReportedAtUtc: null);
			await ProjectToLegacyLogAsync(message.Id, senderLegacyUserId, MessageStatus.Seen, boundaryUtc, ct);
			applied.Add(new AppliedMark(message.SenderId, senderLegacyUserId, IsSeen: true, boundaryUtc, message.Id));
		}

		if (syncBoundary is not null)
		{
			await MarkOwnSendSeenInSequenceSpaceAsync(stintId, accountId, access, syncBoundary, markedSyncMessageIds!, ct);
		}

		if (applied.Count > 0 || markedSyncMessageIds?.Count > 0)
		{
			// Same reason as the receipt path: flushed first, so the recomputation sees the marks this
			// send just made.
			await db.SaveChangesAsync(ct);
			await RecomputeUnreadAsync(access, legacyChatId, senderLegacyUserId, accountId, ct);
		}

		return Group(applied);
	}

	/// <summary>
	/// Incoming messages inside this caller's still-open windows that carry no logical Seen yet. The
	/// legacy log is consulted as well, so a message this person read before stage 10 is not resurrected
	/// as unread by the new model.
	/// </summary>
	private async Task<IReadOnlyList<MessageEntity>> UnseenInCurrentWindowsAsync(
		ChatAccess access, int legacyChatId, int legacyUserId, string accountId, CancellationToken ct)
	{
		LegacyHistoryWindow[] current = [.. access.Windows.Where(w => !w.IsFrozen)];
		if (current.Length == 0)
		{
			return [];
		}

		string chatId = SyncChatIdentity.IdOf(legacyChatId);
		DateTime? from = current.Any(w => w.FromUtc is null) ? null : current.Min(w => w.FromUtc);
		DateTime? through = current.Any(w => w.ThroughUtc is null) ? null : current.Max(w => w.ThroughUtc);

		List<MessageEntity> candidates = await db.Messages.AsNoTracking()
			.Where(m => m.ChatId == legacyChatId
				&& m.SenderId != legacyUserId
				&& (from == null || m.CreatedAt >= from)
				&& (through == null || m.CreatedAt <= through)
				&& !db.SyncLegacyMessageRecipientStates.Any(s =>
					s.ChatId == chatId && s.LegacyMessageId == m.Id && s.SeenAtUtc != null && s.AccountId == accountId)
				&& !m.MessageStatusEntities.Any(s => s.ReceiverId == legacyUserId && s.Status == MessageStatus.Seen))
			.OrderBy(m => m.Id)
			.ToListAsync(ct);

		// The exact per-message rule, after the bounded scan above: a gap between two windows is covered
		// by no window at all and must stay untouched.
		return [.. candidates.Where(m => current.Any(w => w.Contains(m.CreatedAt)))];
	}

	/// <summary>
	/// The unread counter of this caller's CURRENT stint, recomputed from what it actually still has
	/// unread. Deriving it (instead of decrementing) is what makes "намалява еднократно" true under a
	/// retried receipt, two devices reporting at once, and a restart in between: the value cannot drift.
	///
	/// Both message spaces are counted into the ONE counter the stint has: a chat holds legacy rows
	/// written before stage 11 and v1 rows written after it, and recomputing from only one of them would
	/// silently erase the other's contribution. Each space keeps its own pair of pointers.
	/// </summary>
	private async Task RecomputeUnreadAsync(
		ChatAccess access, int legacyChatId, int legacyUserId, string accountId, CancellationToken ct)
	{
		if (ActiveStintOf(access) is not string stintId)
		{
			return;
		}

		string chatId = SyncChatIdentity.IdOf(legacyChatId);
		IReadOnlyList<MessageEntity> unread = await UnseenInCurrentWindowsAsync(access, legacyChatId, legacyUserId, accountId, ct);
		IReadOnlyList<SyncMessageEntity> unreadSync = await UnseenSyncInCurrentWindowsAsync(access, chatId, accountId, stintId, ct);
		SyncUnreadStateEntity state = await UnreadStateAsync(stintId, chatId, ct);

		int? oldest = unread.Count == 0 ? null : unread[0].Id;
		int? latest = unread.Count == 0 ? null : unread[^1].Id;
		string? oldestSync = unreadSync.Count == 0 ? null : unreadSync[0].Id;
		string? latestSync = unreadSync.Count == 0 ? null : unreadSync[^1].Id;
		long count = unread.Count + unreadSync.Count;

		// The newest of whatever is still unread, across both spaces - the one field the sync priority
		// ordering reads.
		DateTime? latestAt = unread.Count == 0
			? unreadSync.Count == 0 ? null : unreadSync[^1].CreatedAtUtc
			: unreadSync.Count == 0 || unread[^1].CreatedAt >= unreadSync[^1].CreatedAtUtc
				? unread[^1].CreatedAt
				: unreadSync[^1].CreatedAtUtc;

		if (state.Count == count
			&& state.OldestUnreadLegacyMessageId == oldest && state.LatestUnreadLegacyMessageId == latest
			&& state.OldestUnreadMessageId == oldestSync && state.LatestUnreadMessageId == latestSync)
		{
			return;
		}

		state.Count = count;
		state.OldestUnreadLegacyMessageId = oldest;
		state.LatestUnreadLegacyMessageId = latest;
		state.OldestUnreadMessageId = oldestSync;
		state.LatestUnreadMessageId = latestSync;
		state.LatestUnreadCreatedAtUtc = latestAt;
		state.Version++;
	}

	/// <summary>
	/// A brand-new message is unread for every eligible recipient by definition, so this one is an exact
	/// increment rather than a recomputation - a send to a large group must not run one aggregate per
	/// member while holding the chat row.
	/// </summary>
	private async Task IncrementUnreadAsync(string membershipId, string chatId, MessageEntity message, CancellationToken ct)
	{
		SyncUnreadStateEntity state = await UnreadStateAsync(membershipId, chatId, ct);

		state.Count++;
		state.OldestUnreadLegacyMessageId ??= message.Id;
		state.LatestUnreadLegacyMessageId = message.Id;
		state.LatestUnreadCreatedAtUtc = message.CreatedAt;
		state.Version++;
	}

	/// <summary>
	/// Every row helper below looks in the change tracker FIRST. One operation can touch the same row
	/// twice (a batch reporting two kinds for one message, a send to a group, the recomputation right
	/// after a mark), and a row added a moment ago is not in the database yet - a plain query would miss
	/// it and add a second copy, which the primary key would then refuse at the commit.
	/// </summary>
	private async Task<SyncUnreadStateEntity> UnreadStateAsync(string membershipId, string chatId, CancellationToken ct)
	{
		SyncUnreadStateEntity? state = db.SyncUnreadStates.Local.FirstOrDefault(u => u.MembershipId == membershipId)
			?? await db.SyncUnreadStates.FirstOrDefaultAsync(u => u.MembershipId == membershipId, ct);
		if (state is not null)
		{
			return state;
		}

		state = new SyncUnreadStateEntity(membershipId, chatId, version: 0, count: 0);
		db.SyncUnreadStates.Add(state);
		return state;
	}

	private async Task<SyncLegacyMessageRecipientStateEntity> RecipientStateAsync(
		string membershipId, string accountId, string chatId, int legacyMessageId, CancellationToken ct)
	{
		SyncLegacyMessageRecipientStateEntity? state = db.SyncLegacyMessageRecipientStates.Local
			.FirstOrDefault(s => s.MembershipId == membershipId && s.LegacyMessageId == legacyMessageId)
			?? await db.SyncLegacyMessageRecipientStates
				.FirstOrDefaultAsync(s => s.MembershipId == membershipId && s.LegacyMessageId == legacyMessageId, ct);
		if (state is not null)
		{
			return state;
		}

		state = new SyncLegacyMessageRecipientStateEntity(membershipId, legacyMessageId, chatId, accountId, version: 0);
		db.SyncLegacyMessageRecipientStates.Add(state);
		return state;
	}

	private async Task<SyncLegacyMessageDeviceStateEntity?> DeviceStateAsync(
		DeviceIdentity device, string membershipId, int legacyChatId, int legacyMessageId, bool create, CancellationToken ct)
	{
		SyncLegacyMessageDeviceStateEntity? state = db.SyncLegacyMessageDeviceStates.Local
			.FirstOrDefault(s => s.LocalInstanceId == device.LocalInstanceId && s.LegacyMessageId == legacyMessageId)
			?? await db.SyncLegacyMessageDeviceStates
				.FirstOrDefaultAsync(s => s.LocalInstanceId == device.LocalInstanceId && s.LegacyMessageId == legacyMessageId, ct);
		if (state is not null || !create)
		{
			return state;
		}

		state = new SyncLegacyMessageDeviceStateEntity(
			legacyMessageId, device.LocalInstanceId, device.DeviceRegistrationId, device.AccountContextId,
			membershipId, SyncChatIdentity.IdOf(legacyChatId), version: 0);
		db.SyncLegacyMessageDeviceStates.Add(state);
		return state;
	}

	/// <summary>
	/// The legacy <c>MessagesStatus</c> log, kept as a PROJECTION of the logical state: the chat history
	/// surface, the old client and the frozen-archive snapshot all still read it. Returns false when an
	/// equivalent row is already there, so a retry cannot append a duplicate.
	/// </summary>
	private async Task<bool> ProjectToLegacyLogAsync(
		int legacyMessageId, int receiverLegacyUserId, MessageStatus status, DateTime atUtc, CancellationToken ct)
	{
		// The local cache first: within one batch the same pair can be touched twice, and a row added a
		// moment ago is not in the database yet.
		bool exists = db.MessagesStatus.Local.Any(s =>
			s.MessageId == legacyMessageId && s.ReceiverId == receiverLegacyUserId && s.Status == status)
			|| await db.MessagesStatus.AnyAsync(s =>
				s.MessageId == legacyMessageId && s.ReceiverId == receiverLegacyUserId && s.Status == status, ct);

		if (exists)
		{
			return false;
		}

		db.MessagesStatus.Add(new MessageStatusEntity(legacyMessageId, receiverLegacyUserId, status, atUtc));
		return true;
	}

	/// <summary>Only the messages of THIS chat: an id from another chat is simply absent, which the caller answers NotApplicable for.</summary>
	private async Task<Dictionary<int, MessageEntity>> MessagesOfAsync(int legacyChatId, int[] legacyMessageIds, CancellationToken ct) =>
		await db.Messages.AsNoTracking()
			.Where(m => m.ChatId == legacyChatId && legacyMessageIds.Contains(m.Id))
			.ToDictionaryAsync(m => m.Id, ct);

	/// <summary>The caller's own device, as the server knows it - never taken from the request body.</summary>
	private async Task<DeviceIdentity?> DeviceOfAsync(ClaimsPrincipal caller, CancellationToken ct)
	{
		string? accountId = caller.FindFirstValue(SessionClaimTypes.AccountContextId);
		string? deviceId = caller.FindFirstValue(SessionClaimTypes.DeviceRegistrationId);
		string? instanceId = caller.FindFirstValue(SessionClaimTypes.LocalInstanceId);

		if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(instanceId))
		{
			return null;
		}

		// The claims are server-issued, but these are the rows a foreign key will point at: this is what
		// keeps a receipt from being recorded against another account's device.
		bool owned = await db.SyncLocalInstances.AsNoTracking()
			.AnyAsync(i => i.Id == instanceId && i.DeviceRegistrationId == deviceId && i.DeviceRegistration.AccountId == accountId, ct);

		return owned ? new DeviceIdentity(accountId, deviceId, instanceId) : null;
	}

	/// <summary>The still-open stint of an access answer: the one whose unread counter and logical rows are the current ones.</summary>
	private static string? ActiveStintOf(ChatAccess access) =>
		access.Windows.FirstOrDefault(w => !w.IsFrozen && w.MembershipId is not null)?.MembershipId;

	private static SeenReason? Reason(SyncSeenReason? reason) => reason switch
	{
		SyncSeenReason.Observed => SeenReason.Observed,
		SyncSeenReason.HistoryShared => SeenReason.HistoryShared,
		SyncSeenReason.OwnSend => SeenReason.OwnSend,
		_ => null,
	};

	/// <summary>One mark per (author, recipient, kind), which is the shape the status event carries.</summary>
	private static IReadOnlyList<RecipientStatusMark> Group(IReadOnlyList<AppliedMark> applied) =>
		[.. applied
			.GroupBy(m => (m.AuthorLegacyUserId, m.RecipientLegacyUserId, m.IsSeen))
			.Select(g => new RecipientStatusMark(
				g.Key.AuthorLegacyUserId, g.Key.RecipientLegacyUserId, g.Key.IsSeen, g.Min(m => m.AtUtc),
				[.. g.Select(m => m.LegacyMessageId).Order()]))];

	private void RequireTransaction(string method)
	{
		if (db.Database.CurrentTransaction is null)
		{
			throw new InvalidOperationException(
				$"{method} must run inside the caller's own explicit transaction: its status rows have to commit " +
				"together with the change that produced them.");
		}
	}

	private sealed record DeviceIdentity(string AccountContextId, string DeviceRegistrationId, string LocalInstanceId);

	private sealed record AppliedMark(int AuthorLegacyUserId, int RecipientLegacyUserId, bool IsSeen, DateTime AtUtc, int LegacyMessageId);
}
