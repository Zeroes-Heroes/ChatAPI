using Application.DTOs.Friends;
using Application.UseCases.Abstractions;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

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

		public Task<Friendship?> GetFriendshipByUserIds(int[] userIds) =>
			dbContext.Friendships
				.Where(f => userIds.Contains(f.SenderUserId)
						&& userIds.Contains(f.TargetUserId))
				.FirstOrDefaultAsync();
		public async Task<IEnumerable<FriendDTO>> GetUserFriendships(int userId) => 
			await dbContext.Friendships
				.Where(f => f.SenderUserId == userId || f.TargetUserId == userId)
				.Select(friendship => new FriendDTO(
						friendship.SenderUserId == userId ? friendship.Target!.Email : friendship.Sender!.Email,
						friendship.SenderUserId == userId ? friendship.Target!.Phone : friendship.Sender!.Phone,
						friendship.Status))
				.ToArrayAsync();
	}
}
