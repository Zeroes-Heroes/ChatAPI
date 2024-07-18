using Services.Chat.Models.CreateChat;
using Services.Chat.Models.Events;
using Services.Chat.Models.GetChats;
using Services.Utilities;

namespace Services.Chat.Interface;

public interface IChatService
{
	Task<Result> CreateChat(CreateChatRequest createChatRequestModel);
	Task<Result<GetChatsResponse[]>> GetChats(int userId);
	void SendEventChatCreated(ChatCreatedEvent chatCreatedEventModel);
}
