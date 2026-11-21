using Database.Context;
using Database.Entities;
using Database.Entities.Sync;
using Database.Enums;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Services.History.Models;
using Services.Membership.Models;
using Services.Statuses.Interface;
using Services.Statuses.Models;
using Services.Sync.Contracts;
using Services.Utilities;
using Xunit;

namespace MembershipTests;

/// <summary>
/// plan/10-recipient-status.md against a real PostgreSQL and the real services: stored ciphertext,
/// readiness and a device's own observation are kept apart from the person's logical state; the first
/// accepted Seen fixes reason and time and takes the message out of unread exactly once; a late
/// receipt that no longer applies ends with a final answer; and the group proportions count the people
/// entitled to that exact message, the author excluded.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class RecipientStatusTests(DatabaseFixture fixture)
{
	[Fact]
	public async Task ReceivedReceipt_SetsTheLogicalState_AndARepeatChangesNothing()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		MessageEntity message = await MembershipTestContext.SendLegacyAsync(
			db, group.Chat, group.Founder.LegacyUserId, "hello", context.Clock.GetUtcNow().UtcDateTime);

		Result<ReceiptBatchResult> first = await context.Statuses(db).AcceptReceiptsAsync(
			group.Second.Principal, group.Chat.Id, [new MessageReceipt(message.Id, ReceiptKind.Received)]);

		Assert.True(first.IsSuccess);
		MessageReceiptOutcome outcome = Assert.Single(first.Data.Outcomes);
		Assert.Equal(ReceiptDisposition.Applied, outcome.Disposition);
		Assert.NotNull(outcome.StateVersion);

		SyncLegacyMessageRecipientStateEntity state = await RecipientStateAsync(db, group, group.Second, message.Id);
		Assert.NotNull(state.ReceivedAtUtc);
		Assert.Null(state.SeenAtUtc);

		// The author is told once, and the legacy projection the current UI reads agrees with the
		// logical row.
		RecipientStatusMark mark = Assert.Single(first.Data.Marks);
		Assert.Equal(group.Founder.LegacyUserId, mark.AuthorLegacyUserId);
		Assert.False(mark.IsSeen);
		Assert.Single(await db.MessagesStatus.AsNoTracking()
			.Where(s => s.MessageId == message.Id && s.ReceiverId == group.Second.LegacyUserId).ToListAsync());

		// A retry after a lost response: the same answer, the same version, nothing published again.
		Result<ReceiptBatchResult> again = await context.Statuses(db).AcceptReceiptsAsync(
			group.Second.Principal, group.Chat.Id, [new MessageReceipt(message.Id, ReceiptKind.Received)]);

		Assert.Equal(ReceiptDisposition.AlreadyApplied, Assert.Single(again.Data.Outcomes).Disposition);
		Assert.Equal(outcome.StateVersion!.Value, Assert.Single(again.Data.Outcomes).StateVersion!.Value);
		Assert.Empty(again.Data.Marks);
		Assert.Single(await db.MessagesStatus.AsNoTracking()
			.Where(s => s.MessageId == message.Id && s.ReceiverId == group.Second.LegacyUserId).ToListAsync());
	}

	[Fact]
	public async Task StoredCiphertext_IsPrivateDeviceProgressOnly()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		MessageEntity message = await MembershipTestContext.SendLegacyAsync(
			db, group.Chat, group.Founder.LegacyUserId, "hello", context.Clock.GetUtcNow().UtcDateTime);

		Result<ReceiptBatchResult> result = await context.Statuses(db).AcceptReceiptsAsync(
			group.Second.Principal, group.Chat.Id, [new MessageReceipt(message.Id, ReceiptKind.CiphertextStored)]);

		Assert.Equal(ReceiptDisposition.Applied, Assert.Single(result.Data.Outcomes).Disposition);
		// "Записването само на ciphertext се отчита отделно": the device row moves, the person's state
		// does not, and the author is told nothing.
		Assert.Empty(result.Data.Marks);
		Assert.Empty(await db.SyncLegacyMessageRecipientStates.AsNoTracking()
			.Where(s => s.LegacyMessageId == message.Id).ToListAsync());

		SyncLegacyMessageDeviceStateEntity device = await db.SyncLegacyMessageDeviceStates.AsNoTracking()
			.SingleAsync(s => s.LegacyMessageId == message.Id && s.LocalInstanceId == group.Second.Instance.Id);
		Assert.NotNull(device.CiphertextStoredAtUtc);
		Assert.Null(device.ReceivedAtUtc);
	}

	[Fact]
	public async Task ASecondDeviceProvesItsOwnDelivery_WithoutMovingThePersonsState()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		MessageEntity message = await MembershipTestContext.SendLegacyAsync(
			db, group.Chat, group.Founder.LegacyUserId, "hello", context.Clock.GetUtcNow().UtcDateTime);

		await context.Statuses(db).AcceptReceiptsAsync(
			group.Second.Principal, group.Chat.Id, [new MessageReceipt(message.Id, ReceiptKind.Received)]);
		DateTime firstReceivedAt = (await RecipientStateAsync(db, group, group.Second, message.Id)).ReceivedAtUtc!.Value;

		context.Clock.Advance(TimeSpan.FromMinutes(5));
		TestMember otherDevice = await context.AddDeviceAsync(db, group.Second);
		Result<ReceiptBatchResult> result = await context.Statuses(db).AcceptReceiptsAsync(
			otherDevice.Principal, group.Chat.Id, [new MessageReceipt(message.Id, ReceiptKind.Received)]);

		// The new device's own row is created ("Ново устройство доказва собствената си доставка"), while
		// the person's logical state keeps the moment its first device proved.
		Assert.Equal(ReceiptDisposition.Applied, Assert.Single(result.Data.Outcomes).Disposition);
		Assert.Equal(2, await db.SyncLegacyMessageDeviceStates.AsNoTracking()
			.CountAsync(s => s.LegacyMessageId == message.Id));
		Assert.Equal(firstReceivedAt, (await RecipientStateAsync(db, group, group.Second, message.Id)).ReceivedAtUtc!.Value);
		Assert.Empty(result.Data.Marks);
	}

	[Fact]
	public async Task TheFirstSeen_FixesReasonAndTime_AndTakesTheMessageOutOfUnreadOnce()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		MessageEntity message = await SendThroughTheChatAsync(context, db, group, group.Founder, "hello");
		Assert.Equal(1L, await UnreadOfAsync(context, db, group, group.Second));

		Result<ReceiptBatchResult> seen = await context.Statuses(db).AcceptReceiptsAsync(
			group.Second.Principal, group.Chat.Id, [new MessageReceipt(message.Id, ReceiptKind.Seen)]);
		Assert.Equal(ReceiptDisposition.Applied, Assert.Single(seen.Data.Outcomes).Disposition);

		SyncLegacyMessageRecipientStateEntity state = await RecipientStateAsync(db, group, group.Second, message.Id);
		DateTime fixedAt = state.SeenAtUtc!.Value;
		Assert.Equal(SyncSeenReason.Observed, state.SeenReason!.Value);
		// Seen implies received, so the author's view can never go backwards.
		Assert.NotNull(state.ReceivedAtUtc);
		Assert.Equal(0L, await UnreadOfAsync(context, db, group, group.Second));

		// A later observation on ANOTHER device of the same person: no new time, no second decrement.
		context.Clock.Advance(TimeSpan.FromMinutes(10));
		TestMember otherDevice = await context.AddDeviceAsync(db, group.Second);
		Result<ReceiptBatchResult> later = await context.Statuses(db).AcceptReceiptsAsync(
			otherDevice.Principal, group.Chat.Id, [new MessageReceipt(message.Id, ReceiptKind.Seen)]);

		Assert.Empty(later.Data.Marks);
		Assert.Equal(fixedAt, (await RecipientStateAsync(db, group, group.Second, message.Id)).SeenAtUtc!.Value);
		Assert.Equal(0L, await UnreadOfAsync(context, db, group, group.Second));
		Assert.Single(await db.MessagesStatus.AsNoTracking()
			.Where(s => s.MessageId == message.Id && s.ReceiverId == group.Second.LegacyUserId && s.Status == MessageStatus.Seen)
			.ToListAsync());
	}

	[Fact]
	public async Task AReportedObservationTime_IsKeptSeparately_AndIsNotTheAuthoritativeOne()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		MessageEntity message = await SendThroughTheChatAsync(context, db, group, group.Founder, "hello");

		// A device claiming it read the message a day ago cannot move the public moment: no
		// server-authenticated clock sample exists on any surface yet, so the server's own receipt time
		// is what is recorded.
		DateTime claimed = context.Clock.GetUtcNow().UtcDateTime.AddDays(-1);
		await context.Statuses(db).AcceptReceiptsAsync(
			group.Second.Principal, group.Chat.Id,
			[new MessageReceipt(message.Id, ReceiptKind.Seen, new ReportedObservation(claimed, WasOffline: true))]);

		SyncLegacyMessageRecipientStateEntity state = await RecipientStateAsync(db, group, group.Second, message.Id);
		Assert.Equal(claimed, state.SeenClientReportedAtUtc!.Value);
		Assert.Equal(context.Clock.GetUtcNow().UtcDateTime, state.SeenAtUtc!.Value);
		Assert.True(state.SeenAtUtc >= message.CreatedAt);
	}

	[Fact]
	public async Task AReceiptForAFrozenArchive_MovesOnlyThePrivateDeviceRecord()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		MessageEntity message = await MembershipTestContext.SendLegacyAsync(
			db, group.Chat, group.Founder.LegacyUserId, "hello", context.Clock.GetUtcNow().UtcDateTime);

		context.Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.True((await context.Membership(db).RemoveMemberAsync(
			group.Founder.Principal, group.Chat.Id, group.Second.LegacyUserId)).IsSuccess);

		Result<ReceiptBatchResult> result = await context.Statuses(db).AcceptReceiptsAsync(
			group.Second.Principal, group.Chat.Id, [new MessageReceipt(message.Id, ReceiptKind.Seen)]);

		// "Публичните отметки и unread в замразения чат остават непроменени."
		Assert.Equal(ReceiptDisposition.Applied, Assert.Single(result.Data.Outcomes).Disposition);
		Assert.Empty(result.Data.Marks);
		Assert.Empty(await db.SyncLegacyMessageRecipientStates.AsNoTracking()
			.Where(s => s.LegacyMessageId == message.Id && s.AccountId == group.Second.Account.Id).ToListAsync());
		Assert.Single(await db.SyncLegacyMessageDeviceStates.AsNoTracking()
			.Where(s => s.LegacyMessageId == message.Id && s.LocalInstanceId == group.Second.Instance.Id).ToListAsync());
	}

	[Fact]
	public async Task ALateReceiptForAMessageOutsideEveryWindow_EndsWithAFinalAnswer()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		// Added with no previous history: everything before the add is outside every window this member
		// holds, so a receipt about it can never become applicable.
		context.Clock.Advance(TimeSpan.FromMinutes(1));
		MessageEntity before = await MembershipTestContext.SendLegacyAsync(
			db, group.Chat, group.Founder.LegacyUserId, "before", context.Clock.GetUtcNow().UtcDateTime);

		context.Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.True((await context.Membership(db).AddMembersAsync(
			group.Founder.Principal, group.Chat.Id, [group.Outsider.LegacyUserId])).IsSuccess);

		Result<ReceiptBatchResult> result = await context.Statuses(db).AcceptReceiptsAsync(
			group.Outsider.Principal, group.Chat.Id, [new MessageReceipt(before.Id, ReceiptKind.Received)]);

		MessageReceiptOutcome outcome = Assert.Single(result.Data.Outcomes);
		Assert.Equal(ReceiptDisposition.NotApplicable, outcome.Disposition);
		// "NotApplicable has null" - the device has nothing to compare and stops retrying.
		Assert.Null(outcome.StateVersion);
		Assert.Empty(await db.SyncLegacyMessageDeviceStates.AsNoTracking()
			.Where(s => s.LegacyMessageId == before.Id).ToListAsync());
	}

	[Fact]
	public async Task AReceiptForAChatTheCallerIsNotIn_IsRefusedAsNotFound()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		MessageEntity message = await MembershipTestContext.SendLegacyAsync(
			db, group.Chat, group.Founder.LegacyUserId, "hello", context.Clock.GetUtcNow().UtcDateTime);

		Result<ReceiptBatchResult> result = await context.Statuses(db).AcceptReceiptsAsync(
			group.Outsider.Principal, group.Chat.Id, [new MessageReceipt(message.Id, ReceiptKind.Received)]);

		Assert.False(result.IsSuccess);
		Assert.Equal((int)System.Net.HttpStatusCode.NotFound, result.StatusCode);
		Assert.Empty(await db.SyncLegacyMessageDeviceStates.AsNoTracking().ToListAsync());
	}

	[Fact]
	public async Task ARevokedDevice_CannotReportAnything()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		MessageEntity message = await MembershipTestContext.SendLegacyAsync(
			db, group.Chat, group.Founder.LegacyUserId, "hello", context.Clock.GetUtcNow().UtcDateTime);

		SyncDeviceRegistrationEntity device = await db.SyncDeviceRegistrations.SingleAsync(d => d.Id == group.Second.Device.Id);
		device.TrustState = DeviceTrustState.Revoked;
		device.TrustVersion++;
		await db.SaveChangesAsync();

		Result<ReceiptBatchResult> result = await context.Statuses(db).AcceptReceiptsAsync(
			group.Second.Principal, group.Chat.Id, [new MessageReceipt(message.Id, ReceiptKind.Received)]);

		Assert.False(result.IsSuccess);
		Assert.Equal((int)System.Net.HttpStatusCode.Unauthorized, result.StatusCode);
		Assert.Empty(await db.SyncLegacyMessageRecipientStates.AsNoTracking().ToListAsync());
		Assert.Empty(await db.SyncLegacyMessageDeviceStates.AsNoTracking().ToListAsync());
	}

	[Fact]
	public async Task OwnSend_MarksTheOlderIncomingMessagesOfItsOwnSender_AndLeavesLaterOnesUnread()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		MessageEntity older = await SendThroughTheChatAsync(context, db, group, group.Founder, "older");
		Assert.Equal(1L, await UnreadOfAsync(context, db, group, group.Second));

		context.Clock.Advance(TimeSpan.FromMinutes(1));
		await SendThroughTheChatAsync(context, db, group, group.Second, "my own message");

		SyncLegacyMessageRecipientStateEntity state = await RecipientStateAsync(db, group, group.Second, older.Id);
		Assert.Equal(SyncSeenReason.OwnSend, state.SeenReason!.Value);
		Assert.Equal(0L, await UnreadOfAsync(context, db, group, group.Second));

		// Anything that arrives AFTER that boundary stays unread: the send's boundary is its own instant.
		context.Clock.Advance(TimeSpan.FromMinutes(1));
		MessageEntity later = await SendThroughTheChatAsync(context, db, group, group.Founder, "later");
		Assert.Equal(1L, await UnreadOfAsync(context, db, group, group.Second));
		Assert.Empty(await db.SyncLegacyMessageRecipientStates.AsNoTracking()
			.Where(s => s.LegacyMessageId == later.Id && s.AccountId == group.Second.Account.Id && s.SeenAtUtc != null)
			.ToListAsync());
	}

	[Fact]
	public async Task SharedHistory_IsSeenWithItsOwnReason_AndProducesNoUnread()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		MessageEntity older = await SendThroughTheChatAsync(context, db, group, group.Founder, "older");

		context.Clock.Advance(TimeSpan.FromHours(1));
		Assert.True((await context.Membership(db).AddMembersAsync(
			group.Founder.Principal, group.Chat.Id, [group.Outsider.LegacyUserId],
			new SharedHistorySelection(SyncSharedHistoryPeriod.Last24Hours, 0))).IsSuccess);

		SyncLegacyMessageRecipientStateEntity state = await RecipientStateAsync(db, group, group.Outsider, older.Id);
		Assert.Equal(SyncSeenReason.HistoryShared, state.SeenReason!.Value);
		// "без unread и известия за ново входящо"
		Assert.Equal(0L, await UnreadOfAsync(context, db, group, group.Outsider));
	}

	[Fact]
	public async Task Proportions_CountThePeopleEntitledToThatExactMessage_WithTheAuthorExcluded()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		MessageEntity message = await SendThroughTheChatAsync(context, db, group, group.Founder, "hello");
		await context.Statuses(db).AcceptReceiptsAsync(
			group.Second.Principal, group.Chat.Id, [new MessageReceipt(message.Id, ReceiptKind.Seen)]);

		IReadOnlyDictionary<int, MessageStatusView> described = await context.Statuses(db).DescribeAsync(group.Chat.Id, [message.Id]);
		MessageStatusView view = described[message.Id];

		// Three people in the chat, the author is not one of its recipients.
		Assert.Equal(2, view.RecipientCount);
		Assert.Equal(1, view.SeenCount);
		Assert.Equal(1, view.ReceivedCount);
		Assert.DoesNotContain(view.Recipients, r => r.RecipientLegacyUserId == group.Founder.LegacyUserId);

		// A member added afterwards is NOT a recipient of a message that predates its window.
		context.Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.True((await context.Membership(db).AddMembersAsync(
			group.Founder.Principal, group.Chat.Id, [group.Outsider.LegacyUserId])).IsSuccess);

		Assert.Equal(2, (await context.Statuses(db).DescribeAsync(group.Chat.Id, [message.Id]))[message.Id].RecipientCount);

		// A removed member still counts for the messages its own archive permits.
		context.Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.True((await context.Membership(db).RemoveMemberAsync(
			group.Founder.Principal, group.Chat.Id, group.Third.LegacyUserId)).IsSuccess);

		MessageStatusView afterRemoval = (await context.Statuses(db).DescribeAsync(group.Chat.Id, [message.Id]))[message.Id];
		Assert.Equal(2, afterRemoval.RecipientCount);
		Assert.Contains(afterRemoval.Recipients, r => r.RecipientLegacyUserId == group.Second.LegacyUserId);
	}

	[Fact]
	public async Task Unread_IsTheCurrentStintsOwn_AndARejoinDoesNotInheritTheArchives()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		await SendThroughTheChatAsync(context, db, group, group.Founder, "one");
		await SendThroughTheChatAsync(context, db, group, group.Founder, "two");
		Assert.Equal(2L, await UnreadOfAsync(context, db, group, group.Second));

		context.Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.True((await context.Membership(db).RemoveMemberAsync(
			group.Founder.Principal, group.Chat.Id, group.Second.LegacyUserId)).IsSuccess);

		// A frozen stint is not the current state any more, so its unread is excluded from the answer.
		IReadOnlyDictionary<int, long> frozen = await context.Statuses(db).UnreadCountsAsync(group.Second.Principal, [group.Chat.Id]);
		Assert.False(frozen.ContainsKey(group.Chat.Id));

		context.Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.True((await context.Membership(db).AddMembersAsync(
			group.Founder.Principal, group.Chat.Id, [group.Second.LegacyUserId])).IsSuccess);

		// "Непрочетеното само в замразения архив след rejoin се изключва от текущия unread": the new
		// stint starts with no counter of its own at all, and the old one's rows are untouched.
		IReadOnlyDictionary<int, long> rejoined = await context.Statuses(db).UnreadCountsAsync(group.Second.Principal, [group.Chat.Id]);
		Assert.True(!rejoined.ContainsKey(group.Chat.Id) || rejoined[group.Chat.Id] == 0);

		context.Clock.Advance(TimeSpan.FromMinutes(1));
		await SendThroughTheChatAsync(context, db, group, group.Founder, "after the rejoin");
		Assert.Equal(1L, await UnreadOfAsync(context, db, group, group.Second));
	}

	[Fact]
	public async Task ABatchLargerThanTheWireCap_IsRefusedAsAWhole()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		MessageReceipt[] tooMany = [.. Enumerable.Range(1, 51).Select(id => new MessageReceipt(id, ReceiptKind.Received))];

		Result<ReceiptBatchResult> result = await context.Statuses(db).AcceptReceiptsAsync(
			group.Second.Principal, group.Chat.Id, tooMany);

		Assert.False(result.IsSuccess);
		Assert.Equal((int)System.Net.HttpStatusCode.BadRequest, result.StatusCode);
	}

	[Fact]
	public async Task OnlyASeenReceiptMayCarryAnObservation()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		Result<ReceiptBatchResult> result = await context.Statuses(db).AcceptReceiptsAsync(
			group.Second.Principal, group.Chat.Id,
			[new MessageReceipt(1, ReceiptKind.Received, new ReportedObservation(context.Clock.GetUtcNow().UtcDateTime, false))]);

		Assert.False(result.IsSuccess);
		Assert.Equal((int)System.Net.HttpStatusCode.BadRequest, result.StatusCode);
	}

	[Fact]
	public async Task EnteringAChat_MarksWhatIsPermitted_AndAFrozenArchiveMarksNothing()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		MessageEntity message = await SendThroughTheChatAsync(context, db, group, group.Founder, "hello");

		Result<ReceiptBatchResult> entered = await context.Statuses(db).MarkChatObservedAsync(group.Second.Principal, group.Chat.Id);
		Assert.True(entered.IsSuccess);
		Assert.Single(entered.Data.Marks);
		Assert.Equal(SyncSeenReason.Observed, (await RecipientStateAsync(db, group, group.Second, message.Id)).SeenReason!.Value);
		Assert.Equal(0L, await UnreadOfAsync(context, db, group, group.Second));

		// Entering it again is a no-op, and entering it after being removed publishes nothing at all.
		Assert.Empty((await context.Statuses(db).MarkChatObservedAsync(group.Second.Principal, group.Chat.Id)).Data.Marks);

		context.Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.True((await context.Membership(db).RemoveMemberAsync(
			group.Founder.Principal, group.Chat.Id, group.Third.LegacyUserId)).IsSuccess);

		Result<ReceiptBatchResult> archived = await context.Statuses(db).MarkChatObservedAsync(group.Third.Principal, group.Chat.Id);
		Assert.True(archived.IsSuccess);
		Assert.Empty(archived.Data.Marks);
		Assert.Empty(await db.SyncLegacyMessageRecipientStates.AsNoTracking()
			.Where(s => s.AccountId == group.Third.Account.Id && s.SeenAtUtc != null).ToListAsync());
	}

	[Fact]
	public async Task OnAChatWithNoMembershipProjection_TheLegacyLogIsAppliedMonotonically()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember author = await context.CreateMemberAsync(db);
		TestMember reader = await context.CreateMemberAsync(db);
		// Deliberately NOT projected: a chat that predates stage 07 has no stint to key a logical row by,
		// so the legacy MessagesStatus log is both the projection and the whole state there.
		ChatEntity chat = await MembershipTestContext.CreateLegacyChatAsync(
			db, "Legacy", author.LegacyUserId, reader.LegacyUserId);

		MessageEntity first = await MembershipTestContext.SendLegacyAsync(
			db, chat, author.LegacyUserId, "one", context.Clock.GetUtcNow().UtcDateTime);
		MessageEntity second = await MembershipTestContext.SendLegacyAsync(
			db, chat, author.LegacyUserId, "two", context.Clock.GetUtcNow().UtcDateTime);

		// The ordinary order first: a Received, then the observation that upgrades it.
		Result<ReceiptBatchResult> received = await context.Statuses(db).AcceptReceiptsAsync(
			reader.Principal, chat.Id, [new MessageReceipt(second.Id, ReceiptKind.Received)]);
		MessageReceiptOutcome receivedOutcome = Assert.Single(received.Data.Outcomes);
		Assert.Equal(ReceiptDisposition.Applied, receivedOutcome.Disposition);
		// "Applied/AlreadyApplied have StateVersion" - here the pair's own transition count.
		Assert.Equal(1L, receivedOutcome.StateVersion!.Value);

		context.Clock.Advance(TimeSpan.FromMinutes(1));
		Result<ReceiptBatchResult> observed = await context.Statuses(db).MarkChatObservedAsync(reader.Principal, chat.Id);
		Assert.True(observed.IsSuccess);
		Assert.All(observed.Data.Marks, mark => Assert.True(mark.IsSeen));
		Assert.All(observed.Data.Outcomes, outcome => Assert.Equal(2L, outcome.StateVersion!.Value));

		// The late arrival: a Received for messages this person has already SEEN. Publishing it would
		// take the author's tick from Seen back to Delivered, with nothing that could ever restore it.
		context.Clock.Advance(TimeSpan.FromMinutes(1));
		Result<ReceiptBatchResult> late = await context.Statuses(db).AcceptReceiptsAsync(
			reader.Principal, chat.Id,
			[new MessageReceipt(first.Id, ReceiptKind.Received), new MessageReceipt(second.Id, ReceiptKind.Received)]);

		Assert.Equal(2, late.Data.Outcomes.Count);
		Assert.All(late.Data.Outcomes, outcome => Assert.Equal(ReceiptDisposition.AlreadyApplied, outcome.Disposition));
		Assert.All(late.Data.Outcomes, outcome => Assert.Equal(2L, outcome.StateVersion!.Value));
		Assert.Empty(late.Data.Marks);

		// The log itself: no Received row appended underneath an existing Seen.
		MessageStatusEntity onlySeen = Assert.Single(await db.MessagesStatus.AsNoTracking()
			.Where(s => s.MessageId == first.Id && s.ReceiverId == reader.LegacyUserId).ToListAsync());
		Assert.Equal(MessageStatus.Seen, onlySeen.Status);

		MessageStatus[] upgraded = await db.MessagesStatus.AsNoTracking()
			.Where(s => s.MessageId == second.Id && s.ReceiverId == reader.LegacyUserId)
			.OrderBy(s => s.Status)
			.Select(s => s.Status)
			.ToArrayAsync();
		Assert.Equal(new[] { MessageStatus.Received, MessageStatus.Seen }, upgraded);
	}

	[Fact]
	public async Task ASupersededAccountContext_IsNoLongerOneOfAMessagesRecipients()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		MessageEntity message = await SendThroughTheChatAsync(context, db, group, group.Founder, "hello");
		Assert.Equal(2, (await context.Statuses(db).DescribeAsync(group.Chat.Id, [message.Id]))[message.Id].RecipientCount);

		// The third member starts over (plan/06): its account context ends while its stint rows stay
		// exactly where they are - that context can never read this chat again.
		context.Clock.Advance(TimeSpan.FromMinutes(1));
		SyncAccountEntity superseded = await db.SyncAccounts.SingleAsync(a => a.Id == group.Third.Account.Id);
		superseded.EndedAtUtc = context.Clock.GetUtcNow().UtcDateTime;
		await db.SaveChangesAsync();

		MessageStatusView view = (await context.Statuses(db).DescribeAsync(group.Chat.Id, [message.Id]))[message.Id];
		Assert.Equal(1, view.RecipientCount);
		Assert.DoesNotContain(view.Recipients, r => r.RecipientLegacyUserId == group.Third.LegacyUserId);

		// And the ACK of the NEXT message agrees with that page: one denominator, counted per person.
		context.Clock.Advance(TimeSpan.FromMinutes(1));
		MessageEntity next = await SendThroughTheChatAsync(context, db, group, group.Founder, "after");
		Assert.Equal(1, (await context.Statuses(db).DescribeAsync(group.Chat.Id, [next.Id]))[next.Id].RecipientCount);
	}

	[Fact]
	public async Task AFrozenArchive_KeepsTheDenominatorOfEveryoneEntitled_NotOnlyOfThoseWhoHadReported()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		MessageEntity message = await SendThroughTheChatAsync(context, db, group, group.Founder, "hello");

		// Exactly ONE of the two recipients proves delivery. The other never reports anything, which is
		// the ordinary case now that nothing invents a Received on reconnect.
		await context.Statuses(db).AcceptReceiptsAsync(
			group.Second.Principal, group.Chat.Id, [new MessageReceipt(message.Id, ReceiptKind.Received)]);

		context.Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.True((await context.Membership(db).RemoveMemberAsync(
			group.Founder.Principal, group.Chat.Id, group.Third.LegacyUserId)).IsSuccess);

		Result<ChatHistoryPage> archive = await context.History(db).ReadAsync(group.Third.Principal, group.Chat.Id, null, 10, false);
		Assert.True(archive.IsSuccess);
		ChatHistoryMessage frozen = Assert.Single(archive.Data.Messages);
		Assert.True(frozen.IsFrozen);

		// Two people had the right to it and the author is not one of them, so the archive says 1 of 2 -
		// never 1 of 1, which is what counting only the reported pairs produced.
		Assert.Equal(2, frozen.RecipientCount);
		ChatHistoryStatus status = Assert.Single(frozen.Statuses);
		Assert.Equal(group.Second.LegacyUserId, status.ReceiverId);
		Assert.Equal(MessageStatus.Received, status.Status);

		// The entitlement without a status is a real row of the archive, with no timestamps at all.
		string chatId = group.Chat.Id.ToString();
		SyncMembershipEntity stint = await db.SyncMemberships.AsNoTracking()
			.SingleAsync(m => m.ChatId == chatId && m.AccountId == group.Third.Account.Id);
		SyncMembershipLegacyStatusSnapshotEntity own = await db.SyncMembershipLegacyStatusSnapshots.AsNoTracking()
			.SingleAsync(s => s.MembershipId == stint.Id
				&& s.LegacyMessageId == message.Id
				&& s.RecipientLegacyUserId == group.Third.LegacyUserId);
		Assert.Null(own.ReceivedAtUtc);
		Assert.Null(own.SeenAtUtc);

		// The author is never one of its own recipients, in the archive either.
		Assert.Empty(await db.SyncMembershipLegacyStatusSnapshots.AsNoTracking()
			.Where(s => s.MembershipId == stint.Id && s.RecipientLegacyUserId == group.Founder.LegacyUserId)
			.ToListAsync());
	}

	/// <summary>
	/// A message committed the way <c>send-message</c> commits it: inside one trusted transaction that
	/// holds the chat row, with the status half (<see cref="IMessageStatusService.RecordNewMessageAsync"/>)
	/// in the SAME transaction - the exact composition the hub performs.
	/// </summary>
	private static async Task<MessageEntity> SendThroughTheChatAsync(
		MembershipTestContext context, AppDbContext db, SharedHistoryTests.Group group, TestMember sender, string content)
	{
		MessageEntity message = new(content, sender.LegacyUserId, group.Chat.Id, context.Clock.GetUtcNow().UtcDateTime);

		await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync();
		ChatAccess access = await context.Authority(db).LockChatAndAuthorizeAsync(sender.Principal, group.Chat.Id);
		Assert.True(access.CanSendNewMessages);

		db.Messages.Add(message);
		await db.SaveChangesAsync();

		NewMessageStatusEffects effects = await context.Statuses(db).RecordNewMessageAsync(
			sender.Principal, access, group.Chat.Id, message);
		await db.SaveChangesAsync();
		await tx.CommitAsync();

		Assert.True(effects.RecipientCount >= 1);
		return message;
	}

	private static async Task<SyncLegacyMessageRecipientStateEntity> RecipientStateAsync(
		AppDbContext db, SharedHistoryTests.Group group, TestMember member, int legacyMessageId)
	{
		string chatId = group.Chat.Id.ToString();
		return await db.SyncLegacyMessageRecipientStates.AsNoTracking()
			.Where(s => s.ChatId == chatId && s.LegacyMessageId == legacyMessageId && s.AccountId == member.Account.Id)
			.OrderByDescending(s => s.Version)
			.FirstAsync();
	}

	private static async Task<long> UnreadOfAsync(
		MembershipTestContext context, AppDbContext db, SharedHistoryTests.Group group, TestMember member)
	{
		IReadOnlyDictionary<int, long> counts = await context.Statuses(db).UnreadCountsAsync(member.Principal, [group.Chat.Id]);
		return counts.TryGetValue(group.Chat.Id, out long count) ? count : 0;
	}
}
