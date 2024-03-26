using Application.UseCases.Abstractions;
using Domain.Entities;
using Infrastructure.Data;

namespace Infrastructure.Repositoies
{
	public class FriendshipRepository(AppDbContext dbContext) : IFriendshipRepository
	{
		public async Task AddFriendship(int senderUserId, int targetUserId)
		{
			Friendship friendship = new(senderUserId, targetUserId);
			dbContext.Friendships.Add(friendship);
			await dbContext.SaveChangesAsync();
		}
	}
}
