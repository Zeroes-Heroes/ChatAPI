using Database.Context;
using Database.Entities;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Services.Membership.Models;
using Xunit;

namespace MembershipTests;

/// <summary>
/// The one shared access check (plan/07-membership.md: "Общите проверки определят достъпа до съобщения и
/// ключове за HTTP, socket и отложена доставка"), exercised against real committed membership state -
/// including the per-message boundary the reconnect sweep and the future key grants rely on.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ChatAccessAuthorityTests(DatabaseFixture fixture)
{
	[Fact]
	public async Task AddedMember_MayNotReachBackBeforeItsOwnWindow_AndRemovedMemberMayNotReachPastItsRemoval()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember a = await context.CreateMemberAsync(db);
		TestMember b = await context.CreateMemberAsync(db);
		TestMember c = await context.CreateMemberAsync(db);
		TestMember d = await context.CreateMemberAsync(db);
		ChatEntity chat = await MembershipTestContext.CreateLegacyChatAsync(db, "Group", a.LegacyUserId, b.LegacyUserId, c.LegacyUserId);
		await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
		{
			await context.Membership(db).ProjectCreatedChatAsync(chat.Id, chat.Name, [a.LegacyUserId, b.LegacyUserId, c.LegacyUserId]);
			await tx.CommitAsync();
		}

		DateTime beforeChanges = context.Clock.GetUtcNow().UtcDateTime;
		context.Clock.Advance(TimeSpan.FromMinutes(10));
		DateTime addedAt = context.Clock.GetUtcNow().UtcDateTime;
		await context.Membership(db).AddMembersAsync(a.Principal, chat.Id, [d.LegacyUserId]);

		context.Clock.Advance(TimeSpan.FromMinutes(10));
		DateTime removedAt = context.Clock.GetUtcNow().UtcDateTime;
		await context.Membership(db).RemoveMemberAsync(a.Principal, chat.Id, c.LegacyUserId);

		context.Clock.Advance(TimeSpan.FromMinutes(10));
		DateTime afterRemoval = context.Clock.GetUtcNow().UtcDateTime;

		ChatAccess founder = await context.Authority(db).GetAsync(a.Principal, chat.Id);
		Assert.Equal(ChatAccessState.Active, founder.State);
		Assert.True(founder.CanSend);
		Assert.True(founder.PermitsMessageAt(beforeChanges));
		Assert.True(founder.PermitsMessageAt(afterRemoval));

		ChatAccess added = await context.Authority(db).GetAsync(d.Principal, chat.Id);
		Assert.Equal(ChatAccessState.Active, added.State);
		Assert.False(added.PermitsMessageAt(beforeChanges));
		Assert.True(added.PermitsMessageAt(addedAt));
		Assert.True(added.PermitsMessageAt(afterRemoval));

		ChatAccess removed = await context.Authority(db).GetAsync(c.Principal, chat.Id);
		Assert.Equal(ChatAccessState.Frozen, removed.State);
		Assert.True(removed.CanRead);
		Assert.False(removed.CanSend);
		Assert.True(removed.PermitsMessageAt(beforeChanges));
		Assert.True(removed.PermitsMessageAt(removedAt));
		Assert.False(removed.PermitsMessageAt(afterRemoval));
	}

	[Fact]
	public async Task RejoinedMember_KeepsItsOldArchiveAndTheNewPeriod_WithTheGapInBetweenInaccessible()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember a = await context.CreateMemberAsync(db);
		TestMember b = await context.CreateMemberAsync(db);
		TestMember c = await context.CreateMemberAsync(db);
		ChatEntity chat = await MembershipTestContext.CreateLegacyChatAsync(db, "Group", a.LegacyUserId, b.LegacyUserId, c.LegacyUserId);
		await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
		{
			await context.Membership(db).ProjectCreatedChatAsync(chat.Id, chat.Name, [a.LegacyUserId, b.LegacyUserId, c.LegacyUserId]);
			await tx.CommitAsync();
		}

		context.Clock.Advance(TimeSpan.FromMinutes(5));
		DateTime leftAt = context.Clock.GetUtcNow().UtcDateTime;
		await context.Membership(db).LeaveAsync(c.Principal, chat.Id);

		context.Clock.Advance(TimeSpan.FromMinutes(5));
		DateTime duringGap = context.Clock.GetUtcNow().UtcDateTime;

		context.Clock.Advance(TimeSpan.FromMinutes(5));
		DateTime rejoinedAt = context.Clock.GetUtcNow().UtcDateTime;
		Assert.True((await context.Membership(db).AddMembersAsync(a.Principal, chat.Id, [c.LegacyUserId])).IsSuccess);

		ChatAccess access = await context.Authority(db).GetAsync(c.Principal, chat.Id);
		Assert.Equal(ChatAccessState.Active, access.State);
		Assert.Equal(2, access.Windows.Count);
		Assert.True(access.PermitsMessageAt(leftAt));
		Assert.False(access.PermitsMessageAt(duringGap));
		Assert.True(access.PermitsMessageAt(rejoinedAt));

		// A rejoin is a NEW stint, never the old one reopened.
		List<SyncMembershipEntity> stints = await db.SyncMemberships.AsNoTracking()
			.Where(m => m.ChatId == chat.Id.ToString() && m.Account.LegacyUserId == c.LegacyUserId)
			.ToListAsync();
		Assert.Equal(2, stints.Count);
		Assert.Single(stints, s => s.State == Database.Enums.Sync.SyncMembershipState.Active);
	}

	[Fact]
	public async Task LiveAndDeferredRecipients_ExcludeDepartedParticipants_EvenThoughTheirLegacyRowRemains()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember a = await context.CreateMemberAsync(db);
		TestMember b = await context.CreateMemberAsync(db);
		TestMember c = await context.CreateMemberAsync(db);
		ChatEntity chat = await MembershipTestContext.CreateLegacyChatAsync(db, "Group", a.LegacyUserId, b.LegacyUserId, c.LegacyUserId);
		await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
		{
			await context.Membership(db).ProjectCreatedChatAsync(chat.Id, chat.Name, [a.LegacyUserId, b.LegacyUserId, c.LegacyUserId]);
			await tx.CommitAsync();
		}

		await context.Membership(db).RemoveMemberAsync(a.Principal, chat.Id, c.LegacyUserId);

		// The legacy participant row is deliberately kept - that is what preserves the archive - so the
		// recipient list must come from membership, not from it.
		ChatEntity legacy = await db.Chats.AsNoTracking().Include(x => x.Users).SingleAsync(x => x.Id == chat.Id);
		Assert.Contains(legacy.Users, u => u.Id == c.LegacyUserId);

		IReadOnlyList<int> recipients = await context.Authority(db).ActiveLegacyRecipientsAsync(
			chat.Id, [b.LegacyUserId, c.LegacyUserId]);

		Assert.Equal(new[] { b.LegacyUserId }, recipients.ToArray());
	}

	[Fact]
	public async Task ChatWithoutProjection_KeepsItsPreStage07Answer_ForParticipantsAndStrangers()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember a = await context.CreateMemberAsync(db);
		TestMember b = await context.CreateMemberAsync(db);
		TestMember stranger = await context.CreateMemberAsync(db);
		ChatEntity chat = await MembershipTestContext.CreateLegacyChatAsync(db, null, a.LegacyUserId, b.LegacyUserId);

		ChatAccess participant = await context.Authority(db).GetAsync(a.Principal, chat.Id);
		Assert.Equal(ChatAccessState.Active, participant.State);
		Assert.True(participant.PermitsMessageAt(context.Clock.GetUtcNow().UtcDateTime.AddYears(-1)));

		ChatAccess outsider = await context.Authority(db).GetAsync(stranger.Principal, chat.Id);
		Assert.Equal(ChatAccessState.None, outsider.State);
		Assert.False(outsider.CanRead);
		Assert.Empty(outsider.Windows);
	}

	[Fact]
	public async Task SupersededAccountContext_SeesNothing_EvenWithAStintOnTheChat()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember a = await context.CreateMemberAsync(db);
		TestMember b = await context.CreateMemberAsync(db);
		ChatEntity chat = await MembershipTestContext.CreateLegacyChatAsync(db, null, a.LegacyUserId, b.LegacyUserId);
		await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
		{
			await context.Membership(db).ProjectCreatedChatAsync(chat.Id, chat.Name, [a.LegacyUserId, b.LegacyUserId]);
			await tx.CommitAsync();
		}

		// Exactly what a fresh start records (stage 06): this chat is excluded for the NEW context.
		db.SyncLegacyContextExclusions.Add(
			new SyncLegacyContextExclusionEntity(a.Account.Id, chat.Id, context.Clock.GetUtcNow().UtcDateTime));
		await db.SaveChangesAsync();

		ChatAccess access = await context.Authority(db).GetAsync(a.Principal, chat.Id);
		Assert.Equal(ChatAccessState.None, access.State);
		Assert.False(access.CanRead);
		Assert.False(access.CanSend);
	}
}
