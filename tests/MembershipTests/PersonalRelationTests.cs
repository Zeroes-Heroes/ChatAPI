using System.Security.Claims;
using Database.Context;
using Database.Entities;
using Database.Entities.Sync;
using Database.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Services.Contacts.Interface;
using Services.Contacts.Models;
using Services.Membership.Models;
using Services.Utilities;
using Xunit;

namespace MembershipTests;

/// <summary>
/// Stage 09 (plan/09-contacts-privacy.md): personal blocking/removal, the correspondence gate it closes
/// and the profile visibility it changes - over the REAL relation writer, the REAL access authority and
/// the REAL profile resolver against a real PostgreSQL. Only push delivery is a no-op sink (it leaves
/// the process).
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PersonalRelationTests(DatabaseFixture fixture)
{
	[Fact]
	public async Task Blocking_StopsNewPersonalCorrespondence_ButKeepsTheHistoryReadableAndItsStatusTraffic()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		(TestMember a, TestMember b, ChatEntity chat) = await PersonalChatAsync(context, db);

		DateTime before = context.Clock.GetUtcNow().UtcDateTime;
		ChatAccess open = await context.Authority(db).GetAsync(a.Principal, chat.Id);
		Assert.True(open.CanSendNewMessages);
		Assert.False(open.PersonalCorrespondenceSuspended);

		context.Clock.Advance(TimeSpan.FromMinutes(5));
		Result<PersonalRelationChange> blocked = await context.PersonalRelations(db).BlockAsync(a.Principal, b.LegacyUserId);
		Assert.True(blocked.IsSuccess);

		foreach (TestMember side in new[] { a, b })
		{
			ChatAccess access = await context.Authority(db).GetAsync(side.Principal, chat.Id);

			// The one thing a block closes.
			Assert.False(access.CanSendNewMessages);
			Assert.True(access.PersonalCorrespondenceSuspended);

			// ...and the things it must NOT close: the archive is not frozen, the chat stays readable and
			// the stint stays active, which is what keeps Seen/Received flowing for what already exists
			// ("Разрешените стари съобщения остават достъпни и продължават да получават статусни промени").
			Assert.Equal(ChatAccessState.Active, access.State);
			Assert.True(access.CanRead);
			Assert.True(access.CanSend);
			Assert.True(access.PermitsMessageAt(before));
		}
	}

	[Fact]
	public async Task Unblocking_RestoresNewCorrespondence_WithoutHavingStoredThePreviousStatus()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		(TestMember a, TestMember b, ChatEntity chat) = await PersonalChatAsync(context, db);

		await context.PersonalRelations(db).BlockAsync(a.Principal, b.LegacyUserId);
		Assert.False((await context.Authority(db).GetAsync(b.Principal, chat.Id)).CanSendNewMessages);

		await context.PersonalRelations(db).UnblockAsync(a.Principal, b.LegacyUserId);

		// The legacy Accepted row was never overwritten, so it simply applies again.
		Assert.True((await context.Authority(db).GetAsync(a.Principal, chat.Id)).CanSendNewMessages);
		Assert.True((await context.Authority(db).GetAsync(b.Principal, chat.Id)).CanSendNewMessages);
		Assert.Equal(FriendshipStatus.Accepted,
			(await db.Friendships.AsNoTracking().FirstAsync(f => f.SenderUserId == a.LegacyUserId && f.TargetUserId == b.LegacyUserId)).Status);
	}

	[Fact]
	public async Task RemovingAContact_SuspendsCorrespondence_AndLeavesThePersonAddableAgain()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		(TestMember a, TestMember b, ChatEntity chat) = await PersonalChatAsync(context, db);

		// Removal is available to EITHER party - b is the TARGET of the original request here, a is the
		// sender, and the legacy status endpoint only ever let the target act.
		Result<PersonalRelationChange> removed = await context.PersonalRelations(db).RemoveContactAsync(a.Principal, b.LegacyUserId);
		Assert.True(removed.IsSuccess);

		Assert.False((await context.Authority(db).GetAsync(a.Principal, chat.Id)).CanSendNewMessages);
		Assert.False((await context.Authority(db).GetAsync(b.Principal, chat.Id)).CanSendNewMessages);

		// Rejected is the legacy "no current contact, may be requested again" state.
		Assert.Equal(FriendshipStatus.Rejected,
			(await db.Friendships.AsNoTracking().FirstAsync(f => f.SenderUserId == a.LegacyUserId && f.TargetUserId == b.LegacyUserId)).Status);

		// ...and no block was created by a removal: removing is not blocking.
		PersonalBlockPair pair = await MembershipTestContext.Relations(db).BlocksBetweenAsync(a.LegacyUserId, b.LegacyUserId);
		Assert.False(pair.AnyBlock);
	}

	[Fact]
	public async Task PersonalBlock_LeavesASharedGroupCompletelyAlone()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember a = await context.CreateMemberAsync(db);
		TestMember b = await context.CreateMemberAsync(db);
		TestMember c = await context.CreateMemberAsync(db);
		await MembershipTestContext.BefriendAsync(db, a.LegacyUserId, b.LegacyUserId);

		ChatEntity group = await MembershipTestContext.CreateLegacyChatAsync(db, "Trip", a.LegacyUserId, b.LegacyUserId, c.LegacyUserId);
		await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
		{
			await context.Membership(db).ProjectCreatedChatAsync(group.Id, group.Name, [a.LegacyUserId, b.LegacyUserId, c.LegacyUserId]);
			await tx.CommitAsync();
		}

		await context.PersonalRelations(db).BlockAsync(a.Principal, b.LegacyUserId);

		// "Личното блокиране запазва активното участие и кореспонденцията в общи групи."
		foreach (TestMember side in new[] { a, b, c })
		{
			ChatAccess access = await context.Authority(db).GetAsync(side.Principal, group.Id);
			Assert.True(access.CanSendNewMessages);
			Assert.False(access.PersonalCorrespondenceSuspended);
		}
	}

	[Fact]
	public async Task ProfileVisibility_FollowsTheRelation_AndIsDirectional()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember a = await context.CreateMemberAsync(db);
		TestMember b = await context.CreateMemberAsync(db);
		TestMember stranger = await context.CreateMemberAsync(db);
		await MembershipTestContext.BefriendAsync(db, a.LegacyUserId, b.LegacyUserId);

		IProfileVisibility profiles = MembershipTestContext.Profiles(db);

		// A stranger sharing nothing but a group is unknown, with no phone.
		ProfileIdentity unknown = await profiles.ResolveAsync(a.LegacyUserId, stranger.LegacyUserId);
		Assert.False(unknown.IsKnown);
		Assert.Equal(ProfileIdentity.UnknownName, unknown.Name);
		Assert.Equal(string.Empty, unknown.Phone);

		// An accepted contact is known in both directions.
		Assert.True((await profiles.ResolveAsync(a.LegacyUserId, b.LegacyUserId)).IsKnown);
		Assert.True((await profiles.ResolveAsync(b.LegacyUserId, a.LegacyUserId)).IsKnown);

		await context.PersonalRelations(db).BlockAsync(a.Principal, b.LegacyUserId);

		// The blocker keeps its own block-list entry (otherwise the block could never be lifted);
		// the blocked party loses the blocker's profile.
		Assert.True((await profiles.ResolveAsync(a.LegacyUserId, b.LegacyUserId)).IsKnown);
		ProfileIdentity blockerAsSeenByBlocked = await profiles.ResolveAsync(b.LegacyUserId, a.LegacyUserId);
		Assert.False(blockerAsSeenByBlocked.IsKnown);
		Assert.Equal(ProfileIdentity.UnknownName, blockerAsSeenByBlocked.Name);

		// The transposed question the push fan-out asks answers the same way.
		IReadOnlySet<int> knowsA = await profiles.ViewersWhoKnowAsync(a.LegacyUserId, [b.LegacyUserId, stranger.LegacyUserId]);
		Assert.DoesNotContain(b.LegacyUserId, knowsA);
		Assert.DoesNotContain(stranger.LegacyUserId, knowsA);
	}

	[Fact]
	public async Task RemovedContact_BecomesUnknown_WhileAPendingRequestStaysDisclosing()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember a = await context.CreateMemberAsync(db);
		TestMember b = await context.CreateMemberAsync(db);
		TestMember pending = await context.CreateMemberAsync(db);
		await MembershipTestContext.BefriendAsync(db, a.LegacyUserId, b.LegacyUserId);
		db.Friendships.Add(new FriendshipEntity(pending.LegacyUserId, a.LegacyUserId));
		await db.SaveChangesAsync();

		IProfileVisibility profiles = MembershipTestContext.Profiles(db);

		// A pending request HAS to disclose: its recipient needs the identity to answer it at all.
		Assert.True((await profiles.ResolveAsync(a.LegacyUserId, pending.LegacyUserId)).IsKnown);

		await context.PersonalRelations(db).RemoveContactAsync(a.Principal, b.LegacyUserId);

		Assert.False((await profiles.ResolveAsync(a.LegacyUserId, b.LegacyUserId)).IsKnown);
		Assert.False((await profiles.ResolveAsync(b.LegacyUserId, a.LegacyUserId)).IsKnown);
	}

	[Fact]
	public async Task BlockedParty_CannotAddItsBlockerToAGroup_WhileAnyoneElseStillCan()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember blocker = await context.CreateMemberAsync(db);
		TestMember blocked = await context.CreateMemberAsync(db);
		TestMember other = await context.CreateMemberAsync(db);

		await context.PersonalRelations(db).BlockAsync(blocker.Principal, blocked.LegacyUserId);

		ChatEntity group = await MembershipTestContext.CreateLegacyChatAsync(db, "Trip", blocked.LegacyUserId, other.LegacyUserId);
		await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
		{
			await context.Membership(db).ProjectCreatedChatAsync(group.Id, group.Name, [blocked.LegacyUserId, other.LegacyUserId]);
			await tx.CommitAsync();
		}

		Result<ChatMembershipChange> refused = await context.Membership(db).AddMembersAsync(blocked.Principal, group.Id, [blocker.LegacyUserId]);
		Assert.False(refused.IsSuccess);

		Result<ChatMembershipChange> allowed = await context.Membership(db).AddMembersAsync(other.Principal, group.Id, [blocker.LegacyUserId]);
		Assert.True(allowed.IsSuccess);
	}

	[Fact]
	public async Task ALegacyBlockedRow_IsStillHonoured_WithNoBackfill()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember blocker = await context.CreateMemberAsync(db);
		TestMember blocked = await context.CreateMemberAsync(db);

		// Exactly what the pre-stage-09 API could write: Status = Blocked, blocker = TargetUserId.
		await MembershipTestContext.BlockAsync(db, blocker.LegacyUserId, blocked.LegacyUserId);

		PersonalBlockPair pair = await MembershipTestContext.Relations(db).BlocksBetweenAsync(blocked.LegacyUserId, blocker.LegacyUserId);
		Assert.True(pair.SubjectBlockedViewer);
		Assert.False(pair.ViewerBlockedSubject);

		Assert.False((await MembershipTestContext.Profiles(db).ResolveAsync(blocked.LegacyUserId, blocker.LegacyUserId)).IsKnown);

		// ...and lifting it clears the legacy status too, or the block would survive its own removal.
		await context.PersonalRelations(db).UnblockAsync(blocker.Principal, blocked.LegacyUserId);
		Assert.False((await MembershipTestContext.Relations(db).BlocksBetweenAsync(blocked.LegacyUserId, blocker.LegacyUserId)).AnyBlock);
	}

	[Fact]
	public async Task RepeatedBlockAndUnblock_Converge_WithOneRowPerOrderedPair()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember a = await context.CreateMemberAsync(db);
		TestMember b = await context.CreateMemberAsync(db);

		// A lost response followed by a retry is the normal case, so the second attempt must write the
		// same state rather than a second record of the same fact.
		await context.PersonalRelations(db).BlockAsync(a.Principal, b.LegacyUserId);
		await context.PersonalRelations(db).BlockAsync(a.Principal, b.LegacyUserId);
		Assert.Equal(1, await db.SyncPersonalBlocks.CountAsync(x => x.BlockerUserId == a.LegacyUserId && x.SubjectUserId == b.LegacyUserId));

		await context.PersonalRelations(db).UnblockAsync(a.Principal, b.LegacyUserId);
		await context.PersonalRelations(db).UnblockAsync(a.Principal, b.LegacyUserId);
		Assert.Equal(1, await db.SyncPersonalBlocks.CountAsync(x => x.BlockerUserId == a.LegacyUserId && x.SubjectUserId == b.LegacyUserId));
		Assert.False((await MembershipTestContext.Relations(db).BlocksBetweenAsync(a.LegacyUserId, b.LegacyUserId)).AnyBlock);

		// Re-blocking reuses the same row.
		await context.PersonalRelations(db).BlockAsync(a.Principal, b.LegacyUserId);
		Assert.Equal(1, await db.SyncPersonalBlocks.CountAsync(x => x.BlockerUserId == a.LegacyUserId && x.SubjectUserId == b.LegacyUserId));
		Assert.True((await MembershipTestContext.Relations(db).BlocksBetweenAsync(a.LegacyUserId, b.LegacyUserId)).AnyBlock);
	}

	[Fact]
	public async Task BothParties_GetTheirOwnCopyOfTheChange_OverTheSameResolverHttpUses()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember a = await context.CreateMemberAsync(db);
		TestMember b = await context.CreateMemberAsync(db);
		await MembershipTestContext.BefriendAsync(db, a.LegacyUserId, b.LegacyUserId);

		context.Hub.Sent.Clear();
		await context.PersonalRelations(db).BlockAsync(a.Principal, b.LegacyUserId);

		SentEvent[] published = [.. context.Hub.Sent.Where(e => e.Event == "personal-relation-changed")];
		Assert.Equal(2, published.Length);

		PersonalRelationChange toActor = (PersonalRelationChange)published
			.Single(e => e.Audience.Contains(a.LegacyUserId.ToString())).Payload!;
		PersonalRelationChange toSubject = (PersonalRelationChange)published
			.Single(e => e.Audience.Contains(b.LegacyUserId.ToString())).Payload!;

		// The blocker's copy: it blocked, it keeps the profile, and the legacy "blocker is not the
		// initiator" convention the blocked-contacts list filters by holds.
		Assert.True(toActor.BlockedByMe);
		Assert.False(toActor.IsInitiator);
		Assert.True(toActor.Profile.IsKnown);

		// The blocked party's copy: it did not block, and the blocker is unknown to it.
		Assert.False(toSubject.BlockedByMe);
		Assert.True(toSubject.IsInitiator);
		Assert.False(toSubject.Profile.IsKnown);
		Assert.Equal(ProfileIdentity.UnknownName, toSubject.Profile.Name);

		Assert.True(toActor.CorrespondenceSuspended);
		Assert.True(toSubject.CorrespondenceSuspended);
	}

	[Fact]
	public async Task ARefusedBlock_WritesNothing()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember a = await context.CreateMemberAsync(db);

		Result<PersonalRelationChange> self = await context.PersonalRelations(db).BlockAsync(a.Principal, a.LegacyUserId);
		Assert.False(self.IsSuccess);

		Result<PersonalRelationChange> unknown = await context.PersonalRelations(db).BlockAsync(a.Principal, int.MaxValue);
		Assert.False(unknown.IsSuccess);

		Assert.Equal(0, await db.SyncPersonalBlocks.CountAsync(x => x.BlockerUserId == a.LegacyUserId));
	}

	[Fact]
	public async Task AnUntrustedCaller_CannotChangeARelation()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember a = await context.CreateMemberAsync(db);
		TestMember b = await context.CreateMemberAsync(db);

		// Revoked at the device, which is exactly what TrustedMutation re-checks while holding that row.
		SyncDeviceRegistrationEntity device = await db.SyncDeviceRegistrations.FirstAsync(d => d.Id == a.Device.Id);
		device.TrustState = Database.Enums.Sync.DeviceTrustState.Revoked;
		await db.SaveChangesAsync();

		Result<PersonalRelationChange> refused = await context.PersonalRelations(db).BlockAsync(a.Principal, b.LegacyUserId);

		Assert.False(refused.IsSuccess);
		Assert.Equal((int)System.Net.HttpStatusCode.Unauthorized, refused.StatusCode);
		Assert.Equal(0, await db.SyncPersonalBlocks.CountAsync(x => x.BlockerUserId == a.LegacyUserId));
	}

	/// <summary>Two accepted contacts and the projected personal chat between them.</summary>
	private static async Task<(TestMember A, TestMember B, ChatEntity Chat)> PersonalChatAsync(MembershipTestContext context, AppDbContext db)
	{
		TestMember a = await context.CreateMemberAsync(db);
		TestMember b = await context.CreateMemberAsync(db);
		await MembershipTestContext.BefriendAsync(db, a.LegacyUserId, b.LegacyUserId);

		ChatEntity chat = await MembershipTestContext.CreateLegacyChatAsync(db, null, a.LegacyUserId, b.LegacyUserId);
		await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
		{
			await context.Membership(db).ProjectCreatedChatAsync(chat.Id, chat.Name, [a.LegacyUserId, b.LegacyUserId]);
			await tx.CommitAsync();
		}

		return (a, b, chat);
	}
}

/// <summary>
/// Stage 09's prepared author rights (plan/09-contacts-privacy.md). The operations stay a later stage;
/// what is checked here is the RIGHT, including the case the plan names explicitly - an own message
/// inside a read-only archive.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class MessageAuthorshipTests(DatabaseFixture fixture)
{
	[Fact]
	public async Task AnAuthorKeepsItsRight_OverItsOwnMessageInAFrozenArchive_AndNeverOverSomebodyElsesMessage()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();

		TestMember a = await context.CreateMemberAsync(db);
		TestMember b = await context.CreateMemberAsync(db);
		TestMember c = await context.CreateMemberAsync(db);
		ChatEntity chat = await MembershipTestContext.CreateLegacyChatAsync(db, "Trip", a.LegacyUserId, b.LegacyUserId, c.LegacyUserId);
		await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
		{
			await context.Membership(db).ProjectCreatedChatAsync(chat.Id, chat.Name, [a.LegacyUserId, b.LegacyUserId, c.LegacyUserId]);
			await tx.CommitAsync();
		}

		DateTime inside = context.Clock.GetUtcNow().UtcDateTime;
		MessageEntity mine = await MembershipTestContext.SendLegacyAsync(db, chat, c.LegacyUserId, "mine", inside);
		MessageEntity theirs = await MembershipTestContext.SendLegacyAsync(db, chat, a.LegacyUserId, "theirs", inside);

		context.Clock.Advance(TimeSpan.FromMinutes(10));
		await context.Membership(db).RemoveMemberAsync(a.Principal, chat.Id, c.LegacyUserId);

		context.Clock.Advance(TimeSpan.FromMinutes(10));
		DateTime afterRemoval = context.Clock.GetUtcNow().UtcDateTime;
		MessageEntity later = await MembershipTestContext.SendLegacyAsync(db, chat, a.LegacyUserId, "later", afterRemoval);

		ChatAccess archive = await context.Authority(db).GetAsync(c.Principal, chat.Id);
		Assert.Equal(ChatAccessState.Frozen, archive.State);

		IReadOnlySet<int> revisable = new Services.Authorship.Service.MessageAuthorshipAuthority().RevisableByAuthor(
			c.Principal,
			archive,
			[
				new Services.Authorship.Models.AuthoredMessage(mine.Id, mine.SenderId, mine.CreatedAt),
				new Services.Authorship.Models.AuthoredMessage(theirs.Id, theirs.SenderId, theirs.CreatedAt),
				new Services.Authorship.Models.AuthoredMessage(later.Id, later.SenderId, later.CreatedAt),
			]);

		// "включително за собствените съобщения в архив само за четене"
		Assert.Contains(mine.Id, revisable);
		// Somebody else's message is never the caller's to revise...
		Assert.DoesNotContain(theirs.Id, revisable);
		// ...and neither is anything outside every window this caller still holds.
		Assert.DoesNotContain(later.Id, revisable);
	}

	[Fact]
	public void ACallerWithoutTheDeviceClaims_HoldsNoAuthorRightsAtAll()
	{
		ClaimsPrincipal bare = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "7")], "test"));

		IReadOnlySet<int> revisable = new Services.Authorship.Service.MessageAuthorshipAuthority().RevisableByAuthor(
			bare,
			ChatAccess.UnboundedActive,
			[new Services.Authorship.Models.AuthoredMessage(1, 7, DateTime.UtcNow)]);

		// Fail closed rather than falling back to the legacy user id.
		Assert.Empty(revisable);
	}
}
