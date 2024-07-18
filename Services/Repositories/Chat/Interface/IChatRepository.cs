using Database.Entities;

namespace Services.Repositories.Chat.Interface;

public interface IChatRepository
{
	Task<ChatEntity> AddChat(ChatEntity chatEntity);
	Task<bool> DoesChatExist(int[] userIds);
	Task<ChatEntity[]> GetChats(int userId);
	Task SaveChangesAsync();

}
