using Database.Context;
using Database.Entities;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Services.Chat.Interface;
using Services.Chat.Models.CreateChat;
using Services.Extensions;
using Services.Hubs;
using Services.NotificationDispatch.Interface;
using Services.Utilities;
using Xunit;

namespace MembershipTests;

/// <summary>
/// <c>POST /chat/create-chat</c> as it is actually wired: the REAL <see cref="IChatService"/> resolved
/// from the REAL <see cref="ServiceCollectionExtension.AddServices"/> registrations (so the repositories,
/// the trust checker and the stage-07 membership writer are the production ones, sharing one
/// <see cref="AppDbContext"/> and one transaction), over a real PostgreSQL. Only the two edges a test
/// process cannot have are substituted: the live socket and the push provider.
///
/// What these cases pin down is that the participant set is canonicalised ONCE: the two-friends rule,
/// the duplicate/blocking guard and the chat kind that is then persisted forever must all be decided
/// over the same set, and every id in it must be a real user before the projection tries to provision
/// an account context for it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ChatCreationRulesTests(DatabaseFixture fixture)
{
	[Fact]
	public async Task CreateChat_WithARepeatedParticipantId_IsStillHeldToTheTwoFriendsRule()
	{
		using Harness harness = CreateHarness();
		TestMember creator = await harness.Context.CreateMemberAsync(harness.Db);
		TestMember stranger = await harness.Context.CreateMemberAsync(harness.Db);

		// Repeating an id used to make this a "three participant" request, which skipped the friendship
		// check entirely - and then persisted a Direct chat for the two real people in it, permanently
		// unmanageable because a personal chat refuses every composition change.
		CreateChatRequest request = new()
		{
			UserIds = [creator.LegacyUserId, stranger.LegacyUserId, stranger.LegacyUserId],
			ChatName = "",
		};

		Result result = await harness.Chats.CreateChat(request, creator.LegacyUserId, creator.Principal);

		Assert.False(result.IsSuccess);
		Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
		Assert.Equal(0, await harness.Db.Chats.CountAsync(c => c.Users.Any(u => u.Id == creator.LegacyUserId)));
		Assert.Empty(harness.Context.Hub.Sent);
	}

	[Fact]
	public async Task CreateChat_BetweenTwoFriends_WithARepeatedId_IsOnePersonalChatOfTwo()
	{
		using Harness harness = CreateHarness();
		TestMember creator = await harness.Context.CreateMemberAsync(harness.Db);
		TestMember friend = await harness.Context.CreateMemberAsync(harness.Db);
		await MembershipTestContext.BefriendAsync(harness.Db, creator.LegacyUserId, friend.LegacyUserId);

		CreateChatRequest request = new()
		{
			UserIds = [creator.LegacyUserId, friend.LegacyUserId, friend.LegacyUserId],
			ChatName = "",
		};

		Result result = await harness.Chats.CreateChat(request, creator.LegacyUserId, creator.Principal);

		Assert.True(result.IsSuccess);
		ChatEntity chat = await harness.Db.Chats.AsNoTracking().Include(c => c.Users)
			.SingleAsync(c => c.Users.Any(u => u.Id == creator.LegacyUserId));
		Assert.Equal(2, chat.Users.Count);

		// One participant, listed twice, is one participant everywhere: the kind, the stints and the
		// push audience all follow the canonical set.
		SyncChatEntity projected = await harness.Db.SyncChats.AsNoTracking().SingleAsync(c => c.Id == chat.Id.ToString());
		Assert.Equal(SyncChatKind.Direct, projected.Kind);
		Assert.Equal(2, await harness.Db.SyncMemberships.CountAsync(m => m.ChatId == projected.Id));
		Assert.Equal(
			new[] { creator.LegacyUserId, friend.LegacyUserId }.Order().ToArray(),
			harness.Push.NewChats.Single().ParticipantIds.Order().ToArray());
	}

	[Fact]
	public async Task CreateChat_WithAnUnknownParticipant_IsRefusedAsNotFound_AndWritesNothing()
	{
		using Harness harness = CreateHarness();
		TestMember creator = await harness.Context.CreateMemberAsync(harness.Db);
		TestMember friend = await harness.Context.CreateMemberAsync(harness.Db);
		await MembershipTestContext.BefriendAsync(harness.Db, creator.LegacyUserId, friend.LegacyUserId);
		int unknown = await harness.Db.Users.MaxAsync(u => u.Id) + 1000;

		CreateChatRequest request = new()
		{
			UserIds = [creator.LegacyUserId, friend.LegacyUserId, unknown],
			ChatName = "Trip",
		};

		Result result = await harness.Chats.CreateChat(request, creator.LegacyUserId, creator.Principal);

		// The projection provisions one account context per participant: an id with no Users row would
		// otherwise fail that insert's foreign key in the middle of the creation transaction, leaving the
		// whole request as an unhandled failure instead of this refusal. Same answer AddMembersAsync gives.
		Assert.False(result.IsSuccess);
		Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
		Assert.Equal(0, await harness.Db.Chats.CountAsync(c => c.Users.Any(u => u.Id == creator.LegacyUserId)));
		Assert.False(await harness.Db.SyncAccounts.AnyAsync(a => a.LegacyUserId == unknown));
		Assert.Empty(harness.Context.Hub.Sent);
	}

	[Fact]
	public async Task CreateChat_ForThreeRealParticipants_IsAGroup_AndNeedsNoFriendship()
	{
		using Harness harness = CreateHarness();
		TestMember creator = await harness.Context.CreateMemberAsync(harness.Db);
		TestMember second = await harness.Context.CreateMemberAsync(harness.Db);
		TestMember third = await harness.Context.CreateMemberAsync(harness.Db);

		CreateChatRequest request = new()
		{
			UserIds = [creator.LegacyUserId, second.LegacyUserId, third.LegacyUserId],
			ChatName = "Trip",
		};

		Result result = await harness.Chats.CreateChat(request, creator.LegacyUserId, creator.Principal);

		Assert.True(result.IsSuccess);
		ChatEntity chat = await harness.Db.Chats.AsNoTracking().Include(c => c.Users)
			.SingleAsync(c => c.Users.Any(u => u.Id == creator.LegacyUserId));
		SyncChatEntity projected = await harness.Db.SyncChats.AsNoTracking().SingleAsync(c => c.Id == chat.Id.ToString());
		Assert.Equal(SyncChatKind.Group, projected.Kind);
		Assert.Equal(3, await harness.Db.SyncMemberships.CountAsync(m => m.ChatId == projected.Id));
	}

	private Harness CreateHarness()
	{
		MembershipTestContext context = new(fixture);
		RecordingDispatch push = new();

		ServiceCollection services = new();
		services.AddLogging();
		services.AddServices();
		// ChatService, ChatRepository, the membership writer and the trust checker must share ONE
		// context, because the chat insert, the membership projection and the trust lock are one
		// transaction - which is exactly what the production (scoped) registration gives them.
		services.AddScoped(_ => fixture.CreateContext());
		services.AddSingleton<TimeProvider>(context.Clock);
		services.AddSingleton<IHubContext<BaseHub>>(context.Hub);
		services.AddScoped<INotificationDispatch>(_ => push);

		ServiceProvider provider = services.BuildServiceProvider();
		IServiceScope scope = provider.CreateScope();

		return new Harness(
			provider,
			scope,
			scope.ServiceProvider.GetRequiredService<AppDbContext>(),
			scope.ServiceProvider.GetRequiredService<IChatService>(),
			context,
			push);
	}

	private sealed record Harness(
		ServiceProvider Provider,
		IServiceScope Scope,
		AppDbContext Db,
		IChatService Chats,
		MembershipTestContext Context,
		RecordingDispatch Push) : IDisposable
	{
		public void Dispose()
		{
			Scope.Dispose();
			Provider.Dispose();
		}
	}

	/// <summary>Records the push fan-out instead of reaching Firebase/APNs; the audience it is given is asserted.</summary>
	internal sealed class RecordingDispatch : INotificationDispatch
	{
		public List<NewChatPush> NewChats { get; } = [];

		public Task NotificationForNewChat(int[] chatParticipantIds, int chatCreatorId, int chatId)
		{
			NewChats.Add(new NewChatPush(chatParticipantIds, chatCreatorId, chatId));
			return Task.CompletedTask;
		}

		public Task NotificationForNewMessage(int[] receiversIds, int senderUserId, string message, int chatId) => Task.CompletedTask;

		public Task NotificationForNewFriendshipRequest(int receiverUserId, string name) => Task.CompletedTask;

		public Task NotificationAcceptFriendship(int notificationRecipientId, int requestSenderId) => Task.CompletedTask;

		public Task NotificationForRejectedFriendship(int notificationRecipientId, int requestSenderId) => Task.CompletedTask;

		public Task NotificationForBlockedFriendship(int notificationRecipientId, int requestSenderId) => Task.CompletedTask;
	}

	internal sealed record NewChatPush(int[] ParticipantIds, int CreatorId, int ChatId);
}
