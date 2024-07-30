using Database.Context;
using Database.Entities;
using Microsoft.EntityFrameworkCore;
using Services.Repositories.Chat.Interface;

namespace Services.Repositories.Chat.Repository
{
	internal class ChatRepository(AppDbContext dbContext) : IChatRepository
	{
		public async Task<ChatEntity> AddChat(ChatEntity chatEntity)
		{
			chatEntity = dbContext.Chats.Add(chatEntity).Entity;

			await SaveChangesAsync();
			return chatEntity;
		}

		public Task<bool> DoesChatExist(int[] userIds) =>
			dbContext.Chats.AnyAsync(c => c.Users.Count == userIds.Length && userIds.All(ui => c.Users.Any(cu => cu.Id == ui)));

		public Task<ChatEntity[]> GetChats(int userId) =>
			dbContext.Chats.Where(c => c.Users.Any(cu => cu.Id == userId)).Include(c => c.Users).ToArrayAsync();

		public Task SaveChangesAsync() =>
			 dbContext.SaveChangesAsync();
	}
}
