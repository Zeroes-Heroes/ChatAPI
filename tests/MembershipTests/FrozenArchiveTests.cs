using System.Net;
using Database.Context;
using Database.Entities;
using Database.Entities.Sync;
using Database.Enums;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Services.History.Models;
using Services.History.Service;
using Services.Utilities;
using Xunit;

namespace MembershipTests;

/// <summary>
/// plan/08-history-archives.md, frozen archives: removal and leaving freeze the permitted messages,
/// their public statuses and the chat metadata; the archive is restorable on a newly authorized device;
/// and the private per-device download record never changes a public mark. Real PostgreSQL, real
/// services - only the live socket is a recording sink.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class FrozenArchiveTests(DatabaseFixture fixture)
{
	[Fact]
	public async Task Removal_FreezesTheChatMetadataAndTheCompositionAsOfTheExit()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		context.Clock.Advance(TimeSpan.FromMinutes(5));
		Assert.True((await context.Membership(db).RemoveMemberAsync(group.Founder.Principal, group.Chat.Id, group.Third.LegacyUserId)).IsSuccess);

		SyncMembershipEntity frozen = await FrozenStintAsync(db, group, group.Third);
		SyncMembershipMetadataSnapshotEntity snapshot = await db.SyncMembershipMetadataSnapshots.AsNoTracking()
			.SingleAsync(s => s.MembershipId == frozen.Id);
		Assert.Equal("Group", snapshot.ChatName);
		Assert.Equal(SyncChatKind.Group, snapshot.ChatKindAtSnapshot);
		Assert.Equal(context.Clock.GetUtcNow().UtcDateTime, snapshot.CapturedAtUtc);

		List<SyncMembershipMetadataSnapshotMemberEntity> members = await db.SyncMembershipMetadataSnapshotMembers.AsNoTracking()
			.Where(m => m.MembershipId == frozen.Id)
			.ToListAsync();
		// The two who remained, plus itself recorded as no longer taking part.
		Assert.Equal(3, members.Count);
		Assert.Equal(SyncMembershipState.Frozen, members.Single(m => m.ParticipantAccountId == frozen.AccountId).ParticipantStateAtSnapshot);
		Assert.Equal(2, members.Count(m => m.ParticipantStateAtSnapshot == SyncMembershipState.Active));

		// A later composition change does not reach the archive.
		context.Clock.Advance(TimeSpan.FromMinutes(5));
		Assert.True((await context.Membership(db).AddMembersAsync(group.Founder.Principal, group.Chat.Id, [group.Outsider.LegacyUserId])).IsSuccess);
		Assert.Equal(3, await db.SyncMembershipMetadataSnapshotMembers.AsNoTracking().CountAsync(m => m.MembershipId == frozen.Id));
	}

	[Fact]
	public async Task Removal_FreezesThePublicStatusesOfThePermittedMessages_AndTheArchiveDoesNotDriftAfterwards()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		DateTime sentAt = context.Clock.GetUtcNow().UtcDateTime;
		MessageEntity message = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "hello", sentAt);
		await MembershipTestContext.MarkLegacyStatusAsync(db, message.Id, group.Second.LegacyUserId, MessageStatus.Received, sentAt);
		await MembershipTestContext.MarkLegacyStatusAsync(db, message.Id, group.Third.LegacyUserId, MessageStatus.Received, sentAt);

		context.Clock.Advance(TimeSpan.FromMinutes(5));
		Assert.True((await context.Membership(db).RemoveMemberAsync(group.Founder.Principal, group.Chat.Id, group.Third.LegacyUserId)).IsSuccess);

		SyncMembershipEntity frozen = await FrozenStintAsync(db, group, group.Third);
		List<SyncMembershipLegacyStatusSnapshotEntity> captured = await db.SyncMembershipLegacyStatusSnapshots.AsNoTracking()
			.Where(s => s.MembershipId == frozen.Id)
			.ToListAsync();
		// Every recipient of the permitted message, so the group's proportions stay rebuildable.
		Assert.Equal(2, captured.Count);
		Assert.All(captured, s => Assert.Equal(sentAt, s.ReceivedAtUtc));
		Assert.All(captured, s => Assert.Null(s.SeenAtUtc));

		// The live status keeps moving for the chat's remaining members; the archive must not follow.
		context.Clock.Advance(TimeSpan.FromMinutes(5));
		await MembershipTestContext.MarkLegacyStatusAsync(db, message.Id, group.Second.LegacyUserId, MessageStatus.Seen, context.Clock.GetUtcNow().UtcDateTime);

		Result<ChatHistoryPage> page = await context.History(db).ReadAsync(group.Third.Principal, group.Chat.Id, null, 10, false);
		Assert.True(page.IsSuccess);
		Assert.True(page.Data.IsArchived);
		ChatHistoryMessage archived = Assert.Single(page.Data.Messages);
		Assert.True(archived.IsFrozen);
		Assert.Equal(2, archived.Statuses.Count);
		Assert.All(archived.Statuses, s => Assert.Equal(MessageStatus.Received, s.Status));

		// The same message, read by a member who is still active, shows today's state.
		Result<ChatHistoryPage> live = await context.History(db).ReadAsync(group.Founder.Principal, group.Chat.Id, null, 10, false);
		Assert.True(live.IsSuccess);
		ChatHistoryMessage current = Assert.Single(live.Data.Messages);
		Assert.False(current.IsFrozen);
		Assert.Equal(MessageStatus.Seen, current.Statuses.Single(s => s.ReceiverId == group.Second.LegacyUserId).Status);
	}

	[Fact]
	public async Task FrozenArchive_StopsAtTheRemoval_AndLaterMessagesAreNotReadable()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		MessageEntity before = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "before", context.Clock.GetUtcNow().UtcDateTime);

		context.Clock.Advance(TimeSpan.FromMinutes(5));
		Assert.True((await context.Membership(db).LeaveAsync(group.Third.Principal, group.Chat.Id)).IsSuccess);

		context.Clock.Advance(TimeSpan.FromMinutes(5));
		MessageEntity after = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "after", context.Clock.GetUtcNow().UtcDateTime);

		Result<ChatHistoryPage> page = await context.History(db).ReadAsync(group.Third.Principal, group.Chat.Id, null, 10, false);
		Assert.True(page.IsSuccess);
		Assert.Equal(new[] { before.Id }, page.Data.Messages.Select(m => m.MessageId).ToArray());
		Assert.DoesNotContain(page.Data.Messages, m => m.MessageId == after.Id);
	}

	[Fact]
	public async Task AStranger_GetsTheSameAnswerAsForAChatThatDoesNotExist()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "secret", context.Clock.GetUtcNow().UtcDateTime);

		Result<ChatHistoryPage> refused = await context.History(db).ReadAsync(group.Outsider.Principal, group.Chat.Id, null, 10, false);
		Assert.False(refused.IsSuccess);
		Assert.Equal((int)System.Net.HttpStatusCode.NotFound, refused.StatusCode);

		Result<ArchiveStoreResult> confirm = await context.History(db).ConfirmArchiveStoredAsync(group.Outsider.Principal, group.Chat.Id, [1]);
		Assert.False(confirm.IsSuccess);
	}

	[Fact]
	public async Task ExcludedFreshStartContext_ReadsNothing_EvenWithAStintOnTheChat()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "old", context.Clock.GetUtcNow().UtcDateTime);

		db.SyncLegacyContextExclusions.Add(
			new SyncLegacyContextExclusionEntity(group.Second.Account.Id, group.Chat.Id, context.Clock.GetUtcNow().UtcDateTime));
		await db.SaveChangesAsync();

		Result<ChatHistoryPage> refused = await context.History(db).ReadAsync(group.Second.Principal, group.Chat.Id, null, 10, false);
		Assert.False(refused.IsSuccess);
	}

	[Fact]
	public async Task ArchiveDownload_UpdatesOnlyThePrivateDeviceRecord_AndIsResumableAndIdempotent()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		DateTime sentAt = context.Clock.GetUtcNow().UtcDateTime;
		MessageEntity first = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "one", sentAt);
		MessageEntity second = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "two", sentAt);
		await MembershipTestContext.MarkLegacyStatusAsync(db, first.Id, group.Third.LegacyUserId, MessageStatus.Received, sentAt);

		context.Clock.Advance(TimeSpan.FromMinutes(5));
		Assert.True((await context.Membership(db).RemoveMemberAsync(group.Founder.Principal, group.Chat.Id, group.Third.LegacyUserId)).IsSuccess);

		List<MessageStatusEntity> publicBefore = await db.MessagesStatus.AsNoTracking().OrderBy(s => s.MessageId).ThenBy(s => s.ReceiverId).ThenBy(s => s.Status).ToListAsync();

		Result<ArchiveStoreResult> stored = await context.History(db).ConfirmArchiveStoredAsync(
			group.Third.Principal, group.Chat.Id, [first.Id, second.Id]);
		Assert.True(stored.IsSuccess);
		Assert.Equal(2, stored.Data.Recorded);

		// "Публичните отметки и unread в замразения чат остават непроменени."
		List<MessageStatusEntity> publicAfter = await db.MessagesStatus.AsNoTracking().OrderBy(s => s.MessageId).ThenBy(s => s.ReceiverId).ThenBy(s => s.Status).ToListAsync();
		Assert.Equal(publicBefore.Count, publicAfter.Count);
		Assert.Equal(
			publicBefore.Select(s => (s.MessageId, s.ReceiverId, s.Status, s.Timestamp)).ToArray(),
			publicAfter.Select(s => (s.MessageId, s.ReceiverId, s.Status, s.Timestamp)).ToArray());

		SyncMembershipEntity frozen = await FrozenStintAsync(db, group, group.Third);
		Assert.Equal(2, await db.SyncArchiveDeviceDeliveries.AsNoTracking()
			.CountAsync(d => d.MembershipId == frozen.Id && d.LocalInstanceId == group.Third.Instance.Id));

		// A retried confirmation after a lost response writes nothing new and still succeeds.
		Result<ArchiveStoreResult> retried = await context.History(db).ConfirmArchiveStoredAsync(
			group.Third.Principal, group.Chat.Id, [first.Id, second.Id]);
		Assert.True(retried.IsSuccess);
		Assert.Equal(0, retried.Data.Recorded);
		Assert.Equal(2, retried.Data.AlreadyRecorded);

		// The resumable read then has nothing left to hand this device...
		Result<ChatHistoryPage> remaining = await context.History(db).ReadAsync(group.Third.Principal, group.Chat.Id, null, 10, true);
		Assert.True(remaining.IsSuccess);
		Assert.Empty(remaining.Data.Messages);

		// ...while a full read still returns the archive, flagged as already held here.
		Result<ChatHistoryPage> full = await context.History(db).ReadAsync(group.Third.Principal, group.Chat.Id, null, 10, false);
		Assert.Equal(2, full.Data.Messages.Count);
		Assert.All(full.Data.Messages, m => Assert.True(m.StoredOnThisDevice));
	}

	[Fact]
	public async Task ArchiveRestore_SpendsEachPageOnWhatIsStillMissing_SoAnArchiveLongerThanOnePageFinishes()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		DateTime sentAt = context.Clock.GetUtcNow().UtcDateTime;
		List<MessageEntity> archive = [];
		for (int i = 0; i < 6; i++)
		{
			archive.Add(await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, $"m{i}", sentAt));
		}

		context.Clock.Advance(TimeSpan.FromMinutes(5));
		Assert.True((await context.Membership(db).LeaveAsync(group.Third.Principal, group.Chat.Id)).IsSuccess);

		// The restore already got the newest four and confirmed them.
		int[] alreadyHere = [.. archive.TakeLast(4).Select(m => m.Id)];
		Assert.True((await context.History(db).ConfirmArchiveStoredAsync(group.Third.Principal, group.Chat.Id, alreadyHere)).IsSuccess);

		// The next page must be spent on messages this device still lacks. Filtering a page AFTER
		// scanning it would answer with nothing here - and a client with a bounded page budget would then
		// never reach the oldest messages of an archive longer than that budget.
		Result<ChatHistoryPage> page = await context.History(db).ReadAsync(group.Third.Principal, group.Chat.Id, null, 2, true);
		Assert.True(page.IsSuccess);
		Assert.Equal(new[] { archive[1].Id, archive[0].Id }, page.Data.Messages.Select(m => m.MessageId).ToArray());
		Assert.All(page.Data.Messages, m => Assert.False(m.StoredOnThisDevice));

		// ...and once those are confirmed too, the restore terminates instead of offering them again.
		Assert.True((await context.History(db).ConfirmArchiveStoredAsync(
			group.Third.Principal, group.Chat.Id, [.. page.Data.Messages.Select(m => m.MessageId)])).IsSuccess);
		Result<ChatHistoryPage> done = await context.History(db).ReadAsync(group.Third.Principal, group.Chat.Id, null, 2, true);
		Assert.Empty(done.Data.Messages);
		Assert.Null(done.Data.NextBeforeMessageId);
	}

	[Fact]
	public async Task TwoOverlappingConfirmationsOfTheSamePage_BothSucceed_AndLeaveOneRowPerMessage()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		DateTime sentAt = context.Clock.GetUtcNow().UtcDateTime;
		MessageEntity first = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "one", sentAt);
		MessageEntity second = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "two", sentAt);

		context.Clock.Advance(TimeSpan.FromMinutes(5));
		Assert.True((await context.Membership(db).LeaveAsync(group.Third.Principal, group.Chat.Id)).IsSuccess);

		// Opening the archive twice in a row, or retrying after a lost response, genuinely runs two
		// confirmations of the same ids at once. Each gets its own connection, so neither can see the
		// other's uncommitted insert - a read-then-insert would have both decide to write and the loser
		// fail on PK_SyncArchiveDeviceDeliveries.
		await using AppDbContext firstConnection = context.CreateContext();
		await using AppDbContext secondConnection = context.CreateContext();
		Task<Result<ArchiveStoreResult>> left = context.History(firstConnection)
			.ConfirmArchiveStoredAsync(group.Third.Principal, group.Chat.Id, [first.Id, second.Id]);
		Task<Result<ArchiveStoreResult>> right = context.History(secondConnection)
			.ConfirmArchiveStoredAsync(group.Third.Principal, group.Chat.Id, [first.Id, second.Id]);
		Result<ArchiveStoreResult>[] results = await Task.WhenAll(left, right);

		Assert.All(results, r => Assert.True(r.IsSuccess));
		// Whichever order they landed in, each message was recorded exactly once and the other call
		// reported it as already recorded rather than failing.
		Assert.Equal(2, results.Sum(r => r.Data.Recorded));
		Assert.Equal(2, results.Sum(r => r.Data.AlreadyRecorded));

		SyncMembershipEntity frozen = await FrozenStintAsync(db, group, group.Third);
		Assert.Equal(2, await db.SyncArchiveDeviceDeliveries.AsNoTracking()
			.CountAsync(d => d.MembershipId == frozen.Id && d.LocalInstanceId == group.Third.Instance.Id));
	}

	[Fact]
	public async Task AConfirmationLargerThanOnePage_IsRefused_InsteadOfTurningIntoAnUnboundedScan()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		context.Clock.Advance(TimeSpan.FromMinutes(5));
		Assert.True((await context.Membership(db).LeaveAsync(group.Third.Principal, group.Chat.Id)).IsSuccess);

		int[] tooMany = [.. Enumerable.Range(1, ChatHistoryService.MaxPageSize + 1)];
		Result<ArchiveStoreResult> refused = await context.History(db).ConfirmArchiveStoredAsync(group.Third.Principal, group.Chat.Id, tooMany);

		Assert.False(refused.IsSuccess);
		Assert.Equal((int)HttpStatusCode.BadRequest, refused.StatusCode);
	}

	[Fact]
	public async Task ANewlyAuthorizedDeviceOfTheSameAccount_StillHasToRestoreTheArchive()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		MessageEntity message = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "one", context.Clock.GetUtcNow().UtcDateTime);

		context.Clock.Advance(TimeSpan.FromMinutes(5));
		Assert.True((await context.Membership(db).LeaveAsync(group.Third.Principal, group.Chat.Id)).IsSuccess);
		Assert.True((await context.History(db).ConfirmArchiveStoredAsync(group.Third.Principal, group.Chat.Id, [message.Id])).IsSuccess);

		// A second authorized device of the SAME account: its own SQLite copy holds nothing yet, so the
		// archive is restorable there - "Архивът се възстановява и на ново разрешено устройство" - even
		// though the public marks say the person already received it.
		TestMember secondDevice = await context.AddDeviceAsync(db, group.Third);
		Result<ChatHistoryPage> page = await context.History(db).ReadAsync(secondDevice.Principal, group.Chat.Id, null, 10, true);
		Assert.True(page.IsSuccess);
		ChatHistoryMessage restored = Assert.Single(page.Data.Messages);
		Assert.Equal(message.Id, restored.MessageId);
		Assert.False(restored.StoredOnThisDevice);
	}

	[Fact]
	public async Task HistoryPaging_AdvancesPastAnInaccessibleStretchInsteadOfStalling()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		DateTime start = context.Clock.GetUtcNow().UtcDateTime;
		MessageEntity readable = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "readable", start);

		context.Clock.Advance(TimeSpan.FromMinutes(5));
		Assert.True((await context.Membership(db).LeaveAsync(group.Third.Principal, group.Chat.Id)).IsSuccess);

		// Three messages the departed member may not read, all newer than its archive.
		context.Clock.Advance(TimeSpan.FromMinutes(5));
		for (int i = 0; i < 3; i++)
		{
			await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, $"hidden {i}", context.Clock.GetUtcNow().UtcDateTime);
		}

		// A page whose entire scanned stretch is inaccessible comes back EMPTY but still advances.
		Result<ChatHistoryPage> firstPage = await context.History(db).ReadAsync(group.Third.Principal, group.Chat.Id, null, 2, false);
		Assert.True(firstPage.IsSuccess);
		Assert.Empty(firstPage.Data.Messages);
		Assert.NotNull(firstPage.Data.NextBeforeMessageId);

		Result<ChatHistoryPage> secondPage = await context.History(db).ReadAsync(
			group.Third.Principal, group.Chat.Id, firstPage.Data.NextBeforeMessageId, 2, false);
		Assert.True(secondPage.IsSuccess);
		Assert.Equal(new[] { readable.Id }, secondPage.Data.Messages.Select(m => m.MessageId).ToArray());
		Assert.Null(secondPage.Data.NextBeforeMessageId);
	}

	[Fact]
	public async Task SeveralConsecutiveMemberships_EachResolveAgainstTheirOwnArchive()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);
		TestMember member = group.Third;

		// Stint 1: one message, Received by this member, then it leaves.
		DateTime firstSentAt = context.Clock.GetUtcNow().UtcDateTime;
		MessageEntity first = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "first", firstSentAt);
		await MembershipTestContext.MarkLegacyStatusAsync(db, first.Id, member.LegacyUserId, MessageStatus.Received, firstSentAt);
		context.Clock.Advance(TimeSpan.FromMinutes(10));
		Assert.True((await context.Membership(db).LeaveAsync(member.Principal, group.Chat.Id)).IsSuccess);

		// A gap it may never read.
		context.Clock.Advance(TimeSpan.FromMinutes(10));
		MessageEntity gap = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "gap", context.Clock.GetUtcNow().UtcDateTime);

		// Stint 2: added back with no previous history, one more message, then removed again.
		context.Clock.Advance(TimeSpan.FromMinutes(10));
		Assert.True((await context.Membership(db).AddMembersAsync(group.Founder.Principal, group.Chat.Id, [member.LegacyUserId])).IsSuccess);
		context.Clock.Advance(TimeSpan.FromMinutes(10));
		DateTime secondSentAt = context.Clock.GetUtcNow().UtcDateTime;
		MessageEntity second = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "second", secondSentAt);
		await MembershipTestContext.MarkLegacyStatusAsync(db, second.Id, member.LegacyUserId, MessageStatus.Seen, secondSentAt);
		context.Clock.Advance(TimeSpan.FromMinutes(10));
		Assert.True((await context.Membership(db).RemoveMemberAsync(group.Founder.Principal, group.Chat.Id, member.LegacyUserId)).IsSuccess);

		// Later activity that belongs to neither archive.
		context.Clock.Advance(TimeSpan.FromMinutes(10));
		await MembershipTestContext.MarkLegacyStatusAsync(db, second.Id, group.Second.LegacyUserId, MessageStatus.Seen, context.Clock.GetUtcNow().UtcDateTime);

		Result<ChatHistoryPage> page = await context.History(db).ReadAsync(member.Principal, group.Chat.Id, null, 10, false);
		Assert.True(page.IsSuccess);
		Assert.Equal(new[] { second.Id, first.Id }, page.Data.Messages.Select(m => m.MessageId).ToArray());
		Assert.DoesNotContain(page.Data.Messages, m => m.MessageId == gap.Id);
		Assert.All(page.Data.Messages, m => Assert.True(m.IsFrozen));

		// Each message resolves against the archive of the stint that actually covered it: the first
		// archive never saw the second message, and the second archive never saw the first.
		ChatHistoryMessage firstView = page.Data.Messages.Single(m => m.MessageId == first.Id);
		Assert.Equal(MessageStatus.Received, firstView.Statuses.Single(s => s.ReceiverId == member.LegacyUserId).Status);

		ChatHistoryMessage secondView = page.Data.Messages.Single(m => m.MessageId == second.Id);
		Assert.Equal(MessageStatus.Seen, secondView.Statuses.Single(s => s.ReceiverId == member.LegacyUserId).Status);
		// The other member's Seen landed AFTER this stint was frozen, so the archive must not show it.
		Assert.DoesNotContain(secondView.Statuses, s => s.ReceiverId == group.Second.LegacyUserId);
	}

	private static async Task<SyncMembershipEntity> FrozenStintAsync(AppDbContext db, SharedHistoryTests.Group group, TestMember member)
	{
		string chatId = group.Chat.Id.ToString();
		return await db.SyncMemberships.AsNoTracking()
			.Where(m => m.ChatId == chatId && m.Account.LegacyUserId == member.LegacyUserId && m.State == SyncMembershipState.Frozen)
			.OrderByDescending(m => m.CreatedAtUtc)
			.FirstAsync();
	}
}
