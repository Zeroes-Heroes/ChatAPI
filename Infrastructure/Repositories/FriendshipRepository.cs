using Application.DTOs.Friends;
using Application.UseCases.Abstractions;
using Domain.Entities;
using Domain.Enums;
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

		public Task UpdateFriendshipStatus(int senderUserId, int targetUserId, FriendshipStatus newStatus) =>
			dbContext.Friendships
				.Where(f => f.SenderUserId == senderUserId
						&& f.TargetUserId == targetUserId)
				.ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, newStatus));

		public async Task<IEnumerable<FriendDTO>> GetUserFriendships(int userId, FriendshipStatus? status = null)
		{
			IQueryable<Friendship> query = dbContext.Friendships
				.Where(f => f.SenderUserId == userId || f.TargetUserId == userId);

			if (status.HasValue)
				query = query.Where(f => f.Status == status);

			return await query.Select(friendship => new FriendDTO(

					friendship.SenderUserId == userId
						? friendship.TargetUserId
						: friendship.SenderUserId,

					friendship.SenderUserId == userId
						? friendship.Target!.Email
						: friendship.Sender!.Email,

					friendship.SenderUserId == userId
						? friendship.Target!.Phone
						: friendship.Sender!.Phone,

					friendship.Status)).ToArrayAsync();
		}
	}
}
