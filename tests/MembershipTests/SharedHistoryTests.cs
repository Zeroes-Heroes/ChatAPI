using Database.Context;
using Database.Entities;
using Database.Entities.Sync;
using Database.Enums;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Services.Membership.Models;
using Services.Utilities;
using Xunit;

namespace MembershipTests;

/// <summary>
/// plan/08-history-archives.md, shared-period selection, against a real PostgreSQL and the real
/// services: the period is computed AT the add (last 24 hours, ten days, the adder's calendar day),
/// intersected with the history the adder itself may read - so the adder's own inaccessible stretches
/// stay inaccessible - and the shared older messages are marked logically Seen for the new member
/// without touching the existing messages themselves.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SharedHistoryTests(DatabaseFixture fixture)
{
	[Fact]
	public async Task AddingWithNoPeriod_GrantsNoPreviousHistory_AndWritesNoWindow()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		Group group = await Group.CreateAsync(context, db);
		MessageEntity older = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "before", context.Clock.GetUtcNow().UtcDateTime);

		context.Clock.Advance(TimeSpan.FromHours(1));
		DateTime addedAt = context.Clock.GetUtcNow().UtcDateTime;
		Assert.True((await context.Membership(db).AddMembersAsync(group.Founder.Principal, group.Chat.Id, [group.Outsider.LegacyUserId])).IsSuccess);

		SyncMembershipEntity stint = await StintOfAsync(db, group, group.Outsider);
		Assert.Empty(await db.SyncMembershipSharedHistoryWindows.AsNoTracking().Where(w => w.MembershipId == stint.Id).ToListAsync());

		ChatAccess access = await context.Authority(db).GetAsync(group.Outsider.Principal, group.Chat.Id);
		Assert.False(access.PermitsMessageAt(older.CreatedAt));
		Assert.True(access.PermitsMessageAt(addedAt));

		// Nothing was marked Seen, because nothing older was shared.
		Assert.Empty(await db.MessagesStatus.AsNoTracking().Where(s => s.ReceiverId == group.Outsider.LegacyUserId).ToListAsync());
	}

	[Theory]
	[InlineData(SyncSharedHistoryPeriod.Last24Hours, 20, 30)]
	[InlineData(SyncSharedHistoryPeriod.LastTenDays, 24 * 9, 24 * 11)]
	public async Task NamedPeriod_SharesExactlyWhatFallsInsideIt(SyncSharedHistoryPeriod period, int insideHoursAgo, int outsideHoursAgo)
	{
		MembershipTestContext context = new(fixture);
		// Far enough from any boundary that the calendar-day clamp below cannot interfere.
		context.Clock.Advance(TimeSpan.FromDays(40));
		await using AppDbContext db = context.CreateContext();

		Group group = await Group.CreateAsync(context, db);
		DateTime now = context.Clock.GetUtcNow().UtcDateTime;
		MessageEntity inside = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "inside", now.AddHours(-insideHoursAgo));
		MessageEntity outside = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "outside", now.AddHours(-outsideHoursAgo));

		Assert.True((await context.Membership(db).AddMembersAsync(
			group.Founder.Principal, group.Chat.Id, [group.Outsider.LegacyUserId], new SharedHistorySelection(period, 0))).IsSuccess);

		ChatAccess access = await context.Authority(db).GetAsync(group.Outsider.Principal, group.Chat.Id);
		Assert.True(access.PermitsMessageAt(inside.CreatedAt));
		Assert.False(access.PermitsMessageAt(outside.CreatedAt));

		// The computed boundary is stored, not recomputed per device.
		SyncMembershipEntity stint = await StintOfAsync(db, group, group.Outsider);
		SyncMembershipSharedHistoryWindowEntity window = await db.SyncMembershipSharedHistoryWindows.AsNoTracking()
			.SingleAsync(w => w.MembershipId == stint.Id);
		Assert.Equal(period, window.SelectedPeriod);
		Assert.Equal(now, window.ThroughUtc);
		Assert.True(window.FromUtc <= inside.CreatedAt);
		Assert.True(window.FromUtc > outside.CreatedAt);
	}

	[Fact]
	public async Task CurrentCalendarDay_UsesTheAddersOwnTimeZone_NotTheServersMidnight()
	{
		// 01:00 UTC. For a device at UTC+03:00 the local day started at 01:00 UTC (04:00 local); for a
		// device at UTC-05:00 the same local day started at 05:00 UTC the PREVIOUS day - i.e. the two
		// devices must produce different boundaries from the same server instant.
		MembershipTestContext context = new(fixture, new DateTimeOffset(2026, 6, 15, 1, 0, 0, TimeSpan.Zero));
		await using AppDbContext db = context.CreateContext();

		Group ahead = await Group.CreateAsync(context, db);
		Group behind = await Group.CreateAsync(context, db);

		DateTime now = context.Clock.GetUtcNow().UtcDateTime;
		MessageEntity lateYesterday = await MembershipTestContext.SendLegacyAsync(db, ahead.Chat, ahead.Founder.LegacyUserId, "late", now.AddHours(-3));
		MessageEntity lateYesterdayBehind = await MembershipTestContext.SendLegacyAsync(db, behind.Chat, behind.Founder.LegacyUserId, "late", now.AddHours(-3));

		Assert.True((await context.Membership(db).AddMembersAsync(
			ahead.Founder.Principal, ahead.Chat.Id, [ahead.Outsider.LegacyUserId],
			new SharedHistorySelection(SyncSharedHistoryPeriod.CurrentCalendarDay, 180))).IsSuccess);
		Assert.True((await context.Membership(db).AddMembersAsync(
			behind.Founder.Principal, behind.Chat.Id, [behind.Outsider.LegacyUserId],
			new SharedHistorySelection(SyncSharedHistoryPeriod.CurrentCalendarDay, -300))).IsSuccess);

		// UTC+03:00: the local day began at 01:00 UTC, so a message from 22:00 UTC yesterday is outside it.
		ChatAccess aheadAccess = await context.Authority(db).GetAsync(ahead.Outsider.Principal, ahead.Chat.Id);
		Assert.False(aheadAccess.PermitsMessageAt(lateYesterday.CreatedAt));

		// UTC-05:00: it is still the previous local day, which began at 05:00 UTC - that message is inside.
		ChatAccess behindAccess = await context.Authority(db).GetAsync(behind.Outsider.Principal, behind.Chat.Id);
		Assert.True(behindAccess.PermitsMessageAt(lateYesterdayBehind.CreatedAt));
	}

	[Fact]
	public async Task SharedPeriod_IsIntersectedWithTheAddersOwnHistory_AndKeepsItsInaccessibleGap()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		Group group = await Group.CreateAsync(context, db);
		TestMember adder = group.Second;

		// The adder's own history has a hole: it left and was added back, with no period shared on the
		// rejoin, so the stretch in between is something it may not read itself.
		MessageEntity beforeAdderLeft = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "a", context.Clock.GetUtcNow().UtcDateTime);

		context.Clock.Advance(TimeSpan.FromMinutes(10));
		Assert.True((await context.Membership(db).LeaveAsync(adder.Principal, group.Chat.Id)).IsSuccess);

		context.Clock.Advance(TimeSpan.FromMinutes(10));
		MessageEntity duringGap = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "b", context.Clock.GetUtcNow().UtcDateTime);

		context.Clock.Advance(TimeSpan.FromMinutes(10));
		Assert.True((await context.Membership(db).AddMembersAsync(group.Founder.Principal, group.Chat.Id, [adder.LegacyUserId])).IsSuccess);

		context.Clock.Advance(TimeSpan.FromMinutes(10));
		MessageEntity afterRejoin = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "c", context.Clock.GetUtcNow().UtcDateTime);

		// The adder now shares "the last 24 hours" - which spans the whole stretch above, including its
		// own gap.
		context.Clock.Advance(TimeSpan.FromMinutes(10));
		Assert.True((await context.Membership(db).AddMembersAsync(
			adder.Principal, group.Chat.Id, [group.Outsider.LegacyUserId],
			new SharedHistorySelection(SyncSharedHistoryPeriod.Last24Hours, 0))).IsSuccess);

		ChatAccess shared = await context.Authority(db).GetAsync(group.Outsider.Principal, group.Chat.Id);
		Assert.True(shared.PermitsMessageAt(beforeAdderLeft.CreatedAt));
		// "Недостъпните за него междини остават недостъпни."
		Assert.False(shared.PermitsMessageAt(duringGap.CreatedAt));
		Assert.True(shared.PermitsMessageAt(afterRejoin.CreatedAt));

		// Two disjoint windows, exactly the two stretches the adder itself holds inside the period.
		SyncMembershipEntity stint = await StintOfAsync(db, group, group.Outsider);
		List<SyncMembershipSharedHistoryWindowEntity> windows = await db.SyncMembershipSharedHistoryWindows.AsNoTracking()
			.Where(w => w.MembershipId == stint.Id)
			.OrderBy(w => w.FromUtc)
			.ToListAsync();
		Assert.Equal(2, windows.Count);
		Assert.True(windows[0].ThroughUtc < windows[1].FromUtc);
	}

	[Fact]
	public async Task SharedOlderMessages_AreMarkedSeenForTheNewMember_WithoutTouchingTheMessagesOrOtherRecipients()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		Group group = await Group.CreateAsync(context, db);
		DateTime sentAt = context.Clock.GetUtcNow().UtcDateTime;
		MessageEntity shared = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "shared", sentAt);
		MessageEntity ownByNewcomer = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Outsider.LegacyUserId, "mine", sentAt);
		await MembershipTestContext.MarkLegacyStatusAsync(db, shared.Id, group.Second.LegacyUserId, MessageStatus.Received, sentAt);

		context.Clock.Advance(TimeSpan.FromHours(1));
		DateTime addedAt = context.Clock.GetUtcNow().UtcDateTime;
		Assert.True((await context.Membership(db).AddMembersAsync(
			group.Founder.Principal, group.Chat.Id, [group.Outsider.LegacyUserId],
			new SharedHistorySelection(SyncSharedHistoryPeriod.Last24Hours, 0))).IsSuccess);

		List<MessageStatusEntity> ofNewcomer = await db.MessagesStatus.AsNoTracking()
			.Where(s => s.ReceiverId == group.Outsider.LegacyUserId)
			.ToListAsync();
		// "Споделените стари съобщения се маркират логически Seen за новия участник" - and only those:
		// it is not its own recipient.
		MessageStatusEntity mark = Assert.Single(ofNewcomer);
		Assert.Equal(shared.Id, mark.MessageId);
		Assert.Equal(MessageStatus.Seen, mark.Status);
		Assert.Equal(addedAt, mark.Timestamp);
		Assert.DoesNotContain(ofNewcomer, s => s.MessageId == ownByNewcomer.Id);

		// Another recipient's own status is untouched, and the message rows themselves are unchanged -
		// "Ключовете на съществуващите съобщения се запазват".
		MessageStatusEntity other = await db.MessagesStatus.AsNoTracking()
			.SingleAsync(s => s.ReceiverId == group.Second.LegacyUserId && s.MessageId == shared.Id);
		Assert.Equal(MessageStatus.Received, other.Status);
		MessageEntity reloaded = await db.Messages.AsNoTracking().SingleAsync(m => m.Id == shared.Id);
		Assert.Equal("shared", reloaded.Content);
		Assert.Equal(sentAt, reloaded.CreatedAt);

		// The authors are told, per author, after the commit.
		SentEvent status = Assert.Single(context.Hub.Sent, e => e.Event == "message-status-update");
		Assert.Contains(group.Founder.LegacyUserId.ToString(), status.Audience);
	}

	[Fact]
	public async Task RefusedAdd_WritesNoWindowAndNoSeenMark()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		Group group = await Group.CreateAsync(context, db);
		MessageEntity existing = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "x", context.Clock.GetUtcNow().UtcDateTime);
		await MembershipTestContext.BlockAsync(db, group.Outsider.LegacyUserId, group.Founder.LegacyUserId);

		Result<ChatMembershipChange> refused = await context.Membership(db).AddMembersAsync(
			group.Founder.Principal, group.Chat.Id, [group.Outsider.LegacyUserId],
			new SharedHistorySelection(SyncSharedHistoryPeriod.LastTenDays, 0));

		Assert.False(refused.IsSuccess);
		Assert.Empty(await db.SyncMembershipSharedHistoryWindows.AsNoTracking().ToListAsync());
		Assert.Empty(await db.MessagesStatus.AsNoTracking().Where(s => s.MessageId == existing.Id).ToListAsync());
	}

	[Fact]
	public async Task Rejoin_UnionsTheOldArchiveTheNewPeriodAndTheFuture_WithTheOverlapCountedOnce()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		Group group = await Group.CreateAsync(context, db);
		DateTime start = context.Clock.GetUtcNow().UtcDateTime;
		MessageEntity archived = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "archived", start);

		context.Clock.Advance(TimeSpan.FromMinutes(10));
		Assert.True((await context.Membership(db).RemoveMemberAsync(group.Founder.Principal, group.Chat.Id, group.Second.LegacyUserId)).IsSuccess);

		context.Clock.Advance(TimeSpan.FromMinutes(10));
		MessageEntity whileAway = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "away", context.Clock.GetUtcNow().UtcDateTime);

		context.Clock.Advance(TimeSpan.FromMinutes(10));
		Assert.True((await context.Membership(db).AddMembersAsync(
			group.Founder.Principal, group.Chat.Id, [group.Second.LegacyUserId],
			new SharedHistorySelection(SyncSharedHistoryPeriod.Last24Hours, 0))).IsSuccess);

		context.Clock.Advance(TimeSpan.FromMinutes(10));
		MessageEntity afterRejoin = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "after", context.Clock.GetUtcNow().UtcDateTime);

		ChatAccess access = await context.Authority(db).GetAsync(group.Second.Principal, group.Chat.Id);
		Assert.Equal(ChatAccessState.Active, access.State);
		Assert.True(access.PermitsMessageAt(archived.CreatedAt));
		Assert.True(access.PermitsMessageAt(whileAway.CreatedAt));
		Assert.True(access.PermitsMessageAt(afterRejoin.CreatedAt));

		// The newly shared period covers the old archive too; the overlap resolves to the ACTIVE stint,
		// never to the frozen one - "Новосподеленият период получава текущото разрешено състояние,
		// включително при припокриване."
		LegacyHistoryWindow? overlap = access.WindowAt(archived.CreatedAt);
		Assert.NotNull(overlap);
		Assert.False(overlap!.IsFrozen);
	}

	[Fact]
	public async Task NarrowerNewPeriod_LeavesTheOlderArchiveFrozenRatherThanReplacingIt()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		Group group = await Group.CreateAsync(context, db);
		DateTime start = context.Clock.GetUtcNow().UtcDateTime;
		MessageEntity archived = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "archived", start);

		context.Clock.Advance(TimeSpan.FromHours(2));
		Assert.True((await context.Membership(db).RemoveMemberAsync(group.Founder.Principal, group.Chat.Id, group.Second.LegacyUserId)).IsSuccess);

		// Far enough away that "the current calendar day" cannot reach the archived message.
		context.Clock.Advance(TimeSpan.FromDays(3));
		Assert.True((await context.Membership(db).AddMembersAsync(
			group.Founder.Principal, group.Chat.Id, [group.Second.LegacyUserId],
			new SharedHistorySelection(SyncSharedHistoryPeriod.CurrentCalendarDay, 0))).IsSuccess);

		ChatAccess access = await context.Authority(db).GetAsync(group.Second.Principal, group.Chat.Id);
		// "По-тесен нов период запазва стария архив" - still readable, and still frozen.
		LegacyHistoryWindow? window = access.WindowAt(archived.CreatedAt);
		Assert.NotNull(window);
		Assert.True(window!.IsFrozen);
	}

	[Fact]
	public async Task AnAddCannotShareMoreThanTheAdderHolds_EvenWithTheWidestPeriod()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		Group group = await Group.CreateAsync(context, db);
		DateTime beforeAdderJoined = context.Clock.GetUtcNow().UtcDateTime;
		MessageEntity older = await MembershipTestContext.SendLegacyAsync(db, group.Chat, group.Founder.LegacyUserId, "before", beforeAdderJoined);

		// A member added with no previous history becomes the adder.
		context.Clock.Advance(TimeSpan.FromMinutes(5));
		Assert.True((await context.Membership(db).AddMembersAsync(group.Founder.Principal, group.Chat.Id, [group.Outsider.LegacyUserId])).IsSuccess);

		context.Clock.Advance(TimeSpan.FromMinutes(5));
		TestMember newcomer = await context.CreateMemberAsync(db);
		Assert.True((await context.Membership(db).AddMembersAsync(
			group.Outsider.Principal, group.Chat.Id, [newcomer.LegacyUserId],
			new SharedHistorySelection(SyncSharedHistoryPeriod.LastTenDays, 0))).IsSuccess);

		ChatAccess access = await context.Authority(db).GetAsync(newcomer.Principal, group.Chat.Id);
		Assert.False(access.PermitsMessageAt(older.CreatedAt));
	}

	private static async Task<SyncMembershipEntity> StintOfAsync(AppDbContext db, Group group, TestMember member)
	{
		string chatId = group.Chat.Id.ToString();
		return await db.SyncMemberships.AsNoTracking()
			.Where(m => m.ChatId == chatId && m.Account.LegacyUserId == member.LegacyUserId && m.State == SyncMembershipState.Active)
			.SingleAsync();
	}

	/// <summary>A projected three-person group plus one person who is not in it yet.</summary>
	internal sealed record Group(TestMember Founder, TestMember Second, TestMember Third, TestMember Outsider, ChatEntity Chat)
	{
		public static async Task<Group> CreateAsync(MembershipTestContext context, AppDbContext db)
		{
			TestMember founder = await context.CreateMemberAsync(db);
			TestMember second = await context.CreateMemberAsync(db);
			TestMember third = await context.CreateMemberAsync(db);
			TestMember outsider = await context.CreateMemberAsync(db);
			ChatEntity chat = await MembershipTestContext.CreateLegacyChatAsync(
				db, "Group", founder.LegacyUserId, second.LegacyUserId, third.LegacyUserId);

			await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
			{
				await context.Membership(db).ProjectCreatedChatAsync(
					chat.Id, chat.Name, [founder.LegacyUserId, second.LegacyUserId, third.LegacyUserId]);
				await tx.CommitAsync();
			}

			return new Group(founder, second, third, outsider, chat);
		}
	}
}
