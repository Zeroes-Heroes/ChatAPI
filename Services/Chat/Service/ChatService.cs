using System.Net;
using System.Security.Claims;
using Database.Context;
using Database.Entities;
using Microsoft.AspNetCore.SignalR;
using Services.Chat.Interface;
using Services.Chat.Models.CreateChat;
using Services.Chat.Models.Events;
using Services.Chat.Models.GetChats;
using Services.Hubs;
using Services.NotificationDispatch.Interface;
using Services.Repositories.Chat.Interface;
using Services.Repositories.Friendship.Interface;
using Services.Repositories.User.Interface;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Services.Utilities;
using static Services.Utilities.Statics.LiveEvents;

namespace Services.Chat.Service;

internal class ChatService(
	IChatRepository chatRepository, IUserRepository userRepository, IFriendshipRepository friendshipRepository,
	IHubContext<BaseHub> hubContext, INotificationDispatch notificationDispatch,
	AppDbContext dbContext, ISessionTrustChecker trustChecker, IAccountContextBoundary contextBoundary) : IChatService
{
	public async Task<Result> CreateChat(CreateChatRequest createChatRequest, int chatCreatorId, ClaimsPrincipal actingUser)
	{
		UserEntity[] userEntities = await userRepository.GetUsers(createChatRequest.UserIds);

		Result? validationResult = await ValidateCreateChatRequest(createChatRequest, userEntities);

		if (validationResult is not null)
			return validationResult;

		// Re-validated (session/device/trust-version), locked, immediately before the insert actually
		// commits - the controller's own [Authorize] already ran once, on request entry, but a concurrent
		// revoke of this exact caller can still commit in the gap between that check and this mutation; see
		// TrustedMutation's own doc comment. The broadcast below only ever runs after a successful commit.
		ChatEntity? chatEntity = null;
		TrustCheckResult trust = await TrustedMutation.RunAsync(dbContext, trustChecker, actingUser, async () =>
		{
			chatEntity = await chatRepository.AddChat(new ChatEntity(createChatRequest.ChatName, userEntities));
		});

		if (!trust.IsTrusted)
		{
			return Result.Failure("Session is not currently trusted.", HttpStatusCode.Unauthorized);
		}

		ChatCreatedEvent chatCreatedEvent = new(chatEntity!.Id, chatEntity.Name, chatEntity.Users.Select(u => u.Id).ToArray());
		SendEventChatCreated(chatCreatedEvent);

		try
		{
			await notificationDispatch.NotificationForNewChat(createChatRequest.UserIds, chatCreatorId, chatEntity.Id);
		}
		catch (Exception ex)
		{
			Console.Write(ex);
		}

		return Result.Success();
	}

	private async Task<Result?> ValidateCreateChatRequest(CreateChatRequest createChatRequest, UserEntity[] userEntities)
	{
		if (userEntities.Length < 2)
			return Result.Failure("Can't create a chat with less than two users.", HttpStatusCode.BadRequest);

		bool isTwoPeopleChat = createChatRequest.UserIds.Length == 2;

		if (isTwoPeopleChat)
		{
			bool areUsersFriends = await friendshipRepository.AreUsersFriends(createChatRequest.UserIds);

			if (!areUsersFriends)
				return Result.Failure("Chats can only be created with friends.", HttpStatusCode.BadRequest);
		}

		bool doesChatExist = await chatRepository.DoesChatExist(createChatRequest.UserIds);

		if (doesChatExist)
			return Result.Failure("Chat with the given users already exists.", HttpStatusCode.Conflict);

		return null; // Return null if no errors are found
	}


	public async Task<Result<GetChatsResponse[]>> GetChats(int userId, ClaimsPrincipal actingUser)
	{
		bool doesUserExist = await userRepository.DoesUserExist(userId);

		if (!doesUserExist)
			return Result<GetChatsResponse[]>.Failure("Unable to get chat's for the user.", HttpStatusCode.BadRequest);

		ChatEntity[] chats = await chatRepository.GetChats(userId);

		// Fresh start (plan/06): the legacy tables key on the user id alone, so a new account context
		// must not list the superseded context's chats. Empty for every account that never started over.
		IReadOnlySet<int> excluded = await contextBoundary.ExcludedLegacyChatIdsAsync(actingUser);

		GetChatsResponse[] chatsResponse = chats
			.Where(c => !excluded.Contains(c.Id))
			.Select(c => new GetChatsResponse(c.Id, c.Name, c.Users.Select(u => u.Id).ToArray())).ToArray();

		return Result<GetChatsResponse[]>.Success(chatsResponse);
	}

	public void SendEventChatCreated(ChatCreatedEvent chatCreatedEvent)
	{
		hubContext.Clients.GetUsersByIds(chatCreatedEvent.UserIds).SendAsync(ChatCreated, chatCreatedEvent);
	}
}
