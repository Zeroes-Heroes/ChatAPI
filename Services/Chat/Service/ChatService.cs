using Database.Entities;
using Microsoft.AspNetCore.SignalR;
using Services.Chat.Interface;
using Services.Chat.Models.CreateChat;
using Services.Chat.Models.Events;
using Services.Chat.Models.GetChats;
using Services.Hubs;
using Services.Repositories.Chat.Interface;
using Services.Repositories.Friendship.Interface;
using Services.Repositories.User.Interface;
using Services.Utilities;
using System.Net;
using static Services.Utilities.Statics.LiveEvents;

namespace Services.Chat.Service;

internal class ChatService(IChatRepository chatRepository, IUserRepository userRepository, IFriendshipRepository friendshipRepository, IHubContext<BaseHub> hubContext) : IChatService
{
	public async Task<Result> CreateChat(CreateChatRequest createChatRequest)
	{
		bool isTwoPeopleChat = createChatRequest.UserIds.Length == 2;

		if (isTwoPeopleChat)
		{
			bool areUsersFriends = await friendshipRepository.AreUsersFriends(createChatRequest.UserIds);

			if (!areUsersFriends)
				return Result.Failure("Chats can only be created with friends.", HttpStatusCode.BadRequest);
		}

		UserEntity[] userEntities = await userRepository.GetUsers(createChatRequest.UserIds);

		if (userEntities.Length < 2)
			return Result.Failure("Can't create chat with less than two users.", HttpStatusCode.BadRequest);

		bool doesChatExist = await chatRepository.DoesChatExist(createChatRequest.UserIds);

		if (doesChatExist)
			return Result.Failure("Chat with the given users already exists.", HttpStatusCode.Conflict);

		ChatEntity chatEntity = new(createChatRequest.Name, userEntities);
		chatEntity = await chatRepository.AddChat(chatEntity);
		await chatRepository.SaveChangesAsync();

		ChatCreatedEvent chatCreatedEvent = new(chatEntity.Id, chatEntity.Name, chatEntity.Users.Select(u => u.Id).ToArray());
		SendEventChatCreated(chatCreatedEvent);

		return Result.Success();
	}

	public async Task<Result<GetChatsResponse[]>> GetChats(int userId)
	{
		bool doesUserExist = await userRepository.DoesUserExist(userId);

		if (!doesUserExist)
			return Result<GetChatsResponse[]>.Failure("Unable to get chat's for the user.", HttpStatusCode.BadRequest);

		ChatEntity[] chats = await chatRepository.GetChats(userId);

		GetChatsResponse[] chatsResponse = chats.Select(c => new GetChatsResponse(c.Id, c.Name, c.Users.Select(u => u.Id).ToArray())).ToArray();

		return Result<GetChatsResponse[]>.Success(chatsResponse);
	}

	public void SendEventChatCreated(ChatCreatedEvent chatCreatedEvent)
	{
		foreach (int id in chatCreatedEvent.UserIds)
			hubContext.Clients.GetUserById(id).SendAsync(ChatCreated, chatCreatedEvent);
	}
}
