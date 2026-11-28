using Database.Context;
using Database.Entities;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Services.Membership;
using Services.Membership.Models;
using Services.Utilities;
using Xunit;

namespace MembershipTests;

/// <summary>
/// plan/07-membership.md, against a real PostgreSQL and the real services: creation rules, the three
/// composition mutations, the preserved chat kind/ChatId, the blocking restriction, the no-re-add rule
/// and the idempotence of a retried mutation.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ChatMembershipServiceTests(DatabaseFixture fixture)
{
	[Fact]
	public async Task CreatedGroup_ProjectsOneActiveStintPerParticipant_WithFullHistoryAndChangeJournal()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember creator = await context.CreateMemberAsync(db);
		TestMember second = await context.CreateMemberAsync(db);
		TestMember third = await context.CreateMemberAsync(db);
		ChatEntity chat = await MembershipTestContext.CreateLegacyChatAsync(db, "Trip", creator.LegacyUserId, second.LegacyUserId, third.LegacyUserId);

		await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
		{
			await context.Membership(db).ProjectCreatedChatAsync(chat.Id, chat.Name, [creator.LegacyUserId, second.LegacyUserId, third.LegacyUserId]);
			await tx.CommitAsync();
		}

		string chatId = chat.Id.ToString();
		SyncChatEntity projected = await db.SyncChats.AsNoTracking().SingleAsync(c => c.Id == chatId);
		Assert.Equal(SyncChatKind.Group, projected.Kind);

		List<SyncMembershipEntity> stints = await db.SyncMemberships.AsNoTracking().Where(m => m.ChatId == chatId).ToListAsync();
		Assert.Equal(3, stints.Count);
		Assert.All(stints, s => Assert.Equal(SyncMembershipState.Active, s.State));
		// A founder has no recorded adder and no lower history bound: it may read the chat from its start.
		Assert.All(stints, s => Assert.Null(s.AddedByAccountId));
		Assert.All(stints, s => Assert.Null(s.LegacyHistoryFromUtc));

		List<SyncChatChangeEntity> changes = await db.SyncChatChanges.AsNoTracking().Where(c => c.ChatId == chatId).ToListAsync();
		Assert.Equal(3, changes.Count(c => c.Kind == SyncChangeKind.MembershipEvent && c.MembershipEventKind == SyncMembershipEventKind.Added));
		Assert.Equal(1, changes.Count(c => c.Kind == SyncChangeKind.Chat));
		// Positions are dense and start at 1, the model-wide convention (0 means "nothing applied yet").
		Assert.Equal(new long[] { 1, 2, 3, 4 }, changes.Select(c => c.ChangeSequence).Order().ToArray());
	}

	[Fact]
	public async Task CreatedDirectChat_ForTwoParticipants_IsDirect_AndRefusesCompositionChanges()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember first = await context.CreateMemberAsync(db);
		TestMember second = await context.CreateMemberAsync(db);
		TestMember outsider = await context.CreateMemberAsync(db);
		ChatEntity chat = await MembershipTestContext.CreateLegacyChatAsync(db, null, first.LegacyUserId, second.LegacyUserId);

		await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
		{
			await context.Membership(db).ProjectCreatedChatAsync(chat.Id, chat.Name, [first.LegacyUserId, second.LegacyUserId]);
			await tx.CommitAsync();
		}

		Assert.Equal(SyncChatKind.Direct, (await db.SyncChats.AsNoTracking().SingleAsync(c => c.Id == chat.Id.ToString())).Kind);

		// "Личният чат запазва типа си": no adding, no removing, no leaving.
		Result<ChatMembershipChange> add = await context.Membership(db).AddMembersAsync(first.Principal, chat.Id, [outsider.LegacyUserId]);
		Result<ChatMembershipChange> leave = await context.Membership(db).LeaveAsync(first.Principal, chat.Id);
		Result<ChatMembershipChange> remove = await context.Membership(db).RemoveMemberAsync(first.Principal, chat.Id, second.LegacyUserId);

		Assert.False(add.IsSuccess);
		Assert.False(leave.IsSuccess);
		Assert.False(remove.IsSuccess);
		Assert.Equal(StatusCodes.Status409Conflict, add.StatusCode);
		Assert.Equal(SyncChatKind.Direct, (await db.SyncChats.AsNoTracking().SingleAsync(c => c.Id == chat.Id.ToString())).Kind);
		Assert.Equal(2, await db.SyncMemberships.CountAsync(m => m.ChatId == chat.Id.ToString()));
	}

	[Fact]
	public async Task DuplicateExactActiveMemberSet_IsRefused_RegardlessOfName()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember a = await context.CreateMemberAsync(db);
		TestMember b = await context.CreateMemberAsync(db);
		TestMember c = await context.CreateMemberAsync(db);
		ChatEntity chat = await MembershipTestContext.CreateLegacyChatAsync(db, "First name", a.LegacyUserId, b.LegacyUserId, c.LegacyUserId);

		await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
		{
			await context.Membership(db).ProjectCreatedChatAsync(chat.Id, chat.Name, [a.LegacyUserId, b.LegacyUserId, c.LegacyUserId]);
			await tx.CommitAsync();
		}

		await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
		{
			DuplicateChatMemberSetException refusal = await Assert.ThrowsAsync<DuplicateChatMemberSetException>(
				() => context.Membership(db).GuardChatCreationAsync(a.LegacyUserId, [c.LegacyUserId, b.LegacyUserId, a.LegacyUserId]));
			Assert.Contains(chat.Id.ToString(), refusal.Message, StringComparison.Ordinal);
			await tx.RollbackAsync();
		}
	}

	[Fact]
	public async Task AfterRemoval_TheSameSetIsCreatableAgain_AndTheReducedGroupKeepsItsIdAndKind()
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

		Result<ChatMembershipChange> removal = await context.Membership(db).RemoveMemberAsync(a.Principal, chat.Id, c.LegacyUserId);
		Assert.True(removal.IsSuccess);

		// The group keeps its ChatId and its kind with two active participants left.
		SyncChatEntity after = await db.SyncChats.AsNoTracking().SingleAsync(x => x.Id == chat.Id.ToString());
		Assert.Equal(SyncChatKind.Group, after.Kind);

		// Its ACTIVE set is now {a, b}; the full original set is no longer anyone's composition, so creating
		// a chat with it again is allowed - a group's composition change is not a permanent name reservation.
		await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
		{
			await context.Membership(db).GuardChatCreationAsync(a.LegacyUserId, [a.LegacyUserId, b.LegacyUserId, c.LegacyUserId]);
			await tx.RollbackAsync();
		}

		// ... while the set that IS its current active composition is still refused.
		await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
		{
			await Assert.ThrowsAsync<DuplicateChatMemberSetException>(
				() => context.Membership(db).GuardChatCreationAsync(a.LegacyUserId, [a.LegacyUserId, b.LegacyUserId]));
			await tx.RollbackAsync();
		}
	}

	[Fact]
	public async Task AddedMember_GetsNoPreviousHistory_AndIsRecordedWithItsAdder()
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

		context.Clock.Advance(TimeSpan.FromMinutes(5));
		DateTime addedAt = context.Clock.GetUtcNow().UtcDateTime;

		Result<ChatMembershipChange> result = await context.Membership(db).AddMembersAsync(a.Principal, chat.Id, [d.LegacyUserId]);

		Assert.True(result.IsSuccess);
		Assert.Equal(new[] { d.LegacyUserId }, result.Data.AddedUserIds.ToArray());
		Assert.Contains(d.LegacyUserId, result.Data.ActiveUserIds);

		SyncMembershipEntity stint = await db.SyncMemberships.AsNoTracking()
			.SingleAsync(m => m.ChatId == chat.Id.ToString() && m.Account.LegacyUserId == d.LegacyUserId);
		Assert.Equal(SyncMembershipState.Active, stint.State);
		Assert.Equal(a.Account.Id, stint.AddedByAccountId);
		// No previous history by default; the bound is the moment of the addition.
		Assert.Equal(addedAt, stint.LegacyHistoryFromUtc);

		// Effective immediately: the legacy participant row exists too, so live delivery reaches it at once.
		ChatEntity legacy = await db.Chats.AsNoTracking().Include(x => x.Users).SingleAsync(x => x.Id == chat.Id);
		Assert.Contains(legacy.Users, u => u.Id == d.LegacyUserId);
	}

	[Fact]
	public async Task ActiveMember_IsNeverAddedAgain_AndTheDatabaseEnforcesIt()
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

		Result<ChatMembershipChange> result = await context.Membership(db).AddMembersAsync(a.Principal, chat.Id, [b.LegacyUserId]);
		Assert.False(result.IsSuccess);
		Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
		Assert.Equal(1, await db.SyncMemberships.CountAsync(m => m.ChatId == chat.Id.ToString() && m.Account.LegacyUserId == b.LegacyUserId));

		// The same rule at the database level: a second active stint cannot even be inserted.
		SyncMembershipEntity existing = await db.SyncMemberships.AsNoTracking()
			.SingleAsync(m => m.ChatId == chat.Id.ToString() && m.Account.LegacyUserId == b.LegacyUserId);
		db.SyncMemberships.Add(new SyncMembershipEntity(
			Guid.NewGuid().ToString("D"), existing.ChatId, existing.AccountId, SyncMembershipState.Active, 1, context.Clock.GetUtcNow().UtcDateTime));
		await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
		db.ChangeTracker.Clear();
	}

	[Fact]
	public async Task BlockedAdder_CannotAddItsBlocker_ButAnotherParticipantCan()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember blocker = await context.CreateMemberAsync(db);
		TestMember blocked = await context.CreateMemberAsync(db);
		TestMember other = await context.CreateMemberAsync(db);
		TestMember third = await context.CreateMemberAsync(db);
		ChatEntity chat = await MembershipTestContext.CreateLegacyChatAsync(db, "Group", blocked.LegacyUserId, other.LegacyUserId, third.LegacyUserId);
		await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
		{
			await context.Membership(db).ProjectCreatedChatAsync(chat.Id, chat.Name, [blocked.LegacyUserId, other.LegacyUserId, third.LegacyUserId]);
			await tx.CommitAsync();
		}

		await MembershipTestContext.BlockAsync(db, blocker.LegacyUserId, blocked.LegacyUserId);

		Result<ChatMembershipChange> refused = await context.Membership(db).AddMembersAsync(blocked.Principal, chat.Id, [blocker.LegacyUserId]);
		Assert.False(refused.IsSuccess);
		Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
		Assert.Equal(0, await db.SyncMemberships.CountAsync(m => m.ChatId == chat.Id.ToString() && m.Account.LegacyUserId == blocker.LegacyUserId));

		// "друг участник, когото А не е блокирал, може да го добави"
		Result<ChatMembershipChange> allowed = await context.Membership(db).AddMembersAsync(other.Principal, chat.Id, [blocker.LegacyUserId]);
		Assert.True(allowed.IsSuccess);
		Assert.Contains(blocker.LegacyUserId, allowed.Data.ActiveUserIds);
	}

	[Fact]
	public async Task RemovalAndLeaving_AreRecordedDistinguishably_AndFreezeTheStint()
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

		Assert.True((await context.Membership(db).RemoveMemberAsync(a.Principal, chat.Id, c.LegacyUserId)).IsSuccess);
		Assert.True((await context.Membership(db).LeaveAsync(b.Principal, chat.Id)).IsSuccess);

		SyncMembershipEntity removed = await db.SyncMemberships.AsNoTracking()
			.SingleAsync(m => m.ChatId == chat.Id.ToString() && m.Account.LegacyUserId == c.LegacyUserId);
		Assert.Equal(SyncMembershipState.Frozen, removed.State);
		Assert.Equal(SyncMembershipEndReason.Removed, removed.EndReason);
		Assert.Equal(a.Account.Id, removed.EndedByAccountId);
		Assert.NotNull(removed.EndedAtUtc);
		// Rights changed with the access change.
		Assert.Equal(2, removed.RightsVersion);

		SyncMembershipEntity left = await db.SyncMemberships.AsNoTracking()
			.SingleAsync(m => m.ChatId == chat.Id.ToString() && m.Account.LegacyUserId == b.LegacyUserId);
		Assert.Equal(SyncMembershipEndReason.Left, left.EndReason);
		// A voluntary departure names nobody as the one who ended it.
		Assert.Null(left.EndedByAccountId);

		List<SyncChatChangeEntity> events = await db.SyncChatChanges.AsNoTracking()
			.Where(x => x.ChatId == chat.Id.ToString() && x.Kind == SyncChangeKind.MembershipEvent)
			.ToListAsync();
		Assert.Single(events, e => e.MembershipEventKind == SyncMembershipEventKind.Removed);
		Assert.Single(events, e => e.MembershipEventKind == SyncMembershipEventKind.Left);
	}

	[Fact]
	public async Task RemovedSubject_IsToldWithoutTheActorIdentity_WhileTheOthersKeepIt()
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

		context.Hub.Sent.Clear();
		await context.Membership(db).RemoveMemberAsync(a.Principal, chat.Id, c.LegacyUserId);

		List<SentEvent> published = [.. context.Hub.Sent.Where(e => e.Event == Services.Utilities.Statics.LiveEvents.ChatMembershipChanged)];
		Assert.Equal(2, published.Count);

		SentEvent toSubject = published.Single(e => e.Audience.Contains(c.LegacyUserId.ToString()));
		ChatMembershipChange subjectPayload = Assert.IsType<ChatMembershipChange>(toSubject.Payload);
		Assert.Null(subjectPayload.ActorUserId);
		Assert.Empty(subjectPayload.ActiveUserIds);
		Assert.True(subjectPayload.IsArchived);

		SentEvent toRemaining = published.Single(e => !e.Audience.Contains(c.LegacyUserId.ToString()));
		ChatMembershipChange remainingPayload = Assert.IsType<ChatMembershipChange>(toRemaining.Payload);
		Assert.Equal(a.LegacyUserId, remainingPayload.ActorUserId);
		Assert.Equal(new[] { c.LegacyUserId }, remainingPayload.RemovedUserIds.ToArray());
	}

	[Fact]
	public async Task SingleRemainingMember_CanStillAdd_AndZeroActiveMembersLeavesOnlyArchive()
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

		await context.Membership(db).LeaveAsync(b.Principal, chat.Id);
		await context.Membership(db).RemoveMemberAsync(a.Principal, chat.Id, c.LegacyUserId);

		// One active participant left: it may still add people (plan/07: "При един останал той може да изпраща и добавя").
		Result<ChatMembershipChange> add = await context.Membership(db).AddMembersAsync(a.Principal, chat.Id, [d.LegacyUserId]);
		Assert.True(add.IsSuccess);

		// Everyone leaves: the chat and its kind remain, and nothing but archive access is left.
		await context.Membership(db).LeaveAsync(d.Principal, chat.Id);
		await context.Membership(db).LeaveAsync(a.Principal, chat.Id);

		SyncChatEntity after = await db.SyncChats.AsNoTracking().SingleAsync(x => x.Id == chat.Id.ToString());
		Assert.Equal(SyncChatKind.Group, after.Kind);
		Assert.Equal(0, await db.SyncMemberships.CountAsync(m => m.ChatId == chat.Id.ToString() && m.State == SyncMembershipState.Active));

		// An archive is read-only: no further composition change is possible from it.
		Result<ChatMembershipChange> refused = await context.Membership(db).AddMembersAsync(a.Principal, chat.Id, [b.LegacyUserId]);
		Assert.False(refused.IsSuccess);
		Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
	}

	[Fact]
	public async Task RetriedRemoval_ReturnsTheSameOutcome_WithoutWritingASecondFreeze()
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

		Assert.True((await context.Membership(db).RemoveMemberAsync(a.Principal, chat.Id, c.LegacyUserId)).IsSuccess);
		DateTime? firstFreeze = (await db.SyncMemberships.AsNoTracking()
			.SingleAsync(m => m.ChatId == chat.Id.ToString() && m.Account.LegacyUserId == c.LegacyUserId)).EndedAtUtc;

		context.Clock.Advance(TimeSpan.FromMinutes(1));
		context.Hub.Sent.Clear();

		// The lost-response retry: same request again.
		Result<ChatMembershipChange> retry = await context.Membership(db).RemoveMemberAsync(a.Principal, chat.Id, c.LegacyUserId);

		Assert.True(retry.IsSuccess);
		SyncMembershipEntity stint = await db.SyncMemberships.AsNoTracking()
			.SingleAsync(m => m.ChatId == chat.Id.ToString() && m.Account.LegacyUserId == c.LegacyUserId);
		Assert.Equal(firstFreeze, stint.EndedAtUtc);
		Assert.Equal(2, stint.RightsVersion);
		Assert.Equal(1, await db.SyncMemberships.CountAsync(m => m.ChatId == chat.Id.ToString() && m.Account.LegacyUserId == c.LegacyUserId));
		// Nothing changed, so nothing is published a second time.
		Assert.Empty(context.Hub.Sent);
	}

	[Fact]
	public async Task PreStage07Chat_IsProjectedOnFirstMutation_WithFullHistoryForItsExistingParticipants()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember a = await context.CreateMemberAsync(db);
		TestMember b = await context.CreateMemberAsync(db);
		TestMember c = await context.CreateMemberAsync(db);
		TestMember d = await context.CreateMemberAsync(db);
		// A legacy chat with no projection at all - exactly what exists in a database migrated from before
		// this stage.
		ChatEntity chat = await MembershipTestContext.CreateLegacyChatAsync(db, "Old group", a.LegacyUserId, b.LegacyUserId, c.LegacyUserId);
		Assert.False(await db.SyncChats.AnyAsync(x => x.Id == chat.Id.ToString()));

		Result<ChatMembershipChange> add = await context.Membership(db).AddMembersAsync(a.Principal, chat.Id, [d.LegacyUserId]);

		Assert.True(add.IsSuccess);
		Assert.Equal(SyncChatKind.Group, (await db.SyncChats.AsNoTracking().SingleAsync(x => x.Id == chat.Id.ToString())).Kind);
		List<SyncMembershipEntity> stints = await db.SyncMemberships.AsNoTracking().Where(m => m.ChatId == chat.Id.ToString()).ToListAsync();
		Assert.Equal(4, stints.Count);
		// The three original participants keep the chat's whole history; only the new one is bounded.
		Assert.Equal(3, stints.Count(s => s.LegacyHistoryFromUtc is null));
		Assert.Single(stints, s => s.LegacyHistoryFromUtc is not null);
	}

	/// <summary>Status codes are asserted as numbers, exactly as the controller returns them.</summary>
	private static class StatusCodes
	{
		public const int Status403Forbidden = 403;
		public const int Status409Conflict = 409;
	}
}
