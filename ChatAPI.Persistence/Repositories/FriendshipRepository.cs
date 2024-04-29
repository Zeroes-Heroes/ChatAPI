using ChatAPI.Application.DTOs.Friends;
using ChatAPI.Application.UseCases.Abstractions;
using ChatAPI.Domain.Entities;
using ChatAPI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ChatAPI.Persistence.Database;

namespace ChatAPI.Persistence.Repositories
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

		public async Task<IEnumerable<FriendDTO>> GetUserFriendships(int userId, FriendshipStatus? status = null, bool? isInitiator = null)
		{
			IQueryable<Friendship> query = dbContext.Friendships
				.Where(f => f.SenderUserId == userId || f.TargetUserId == userId);

			if (status.HasValue)
				query = query.Where(f => f.Status == status);	

			if (isInitiator.HasValue)
				query = query.Where(f => isInitiator.Value
								? f.SenderUserId == userId
								: f.TargetUserId == userId);

			return await query.Select(friendship => new FriendDTO(

					friendship.SenderUserId == userId
						? friendship.TargetUserId
						: friendship.SenderUserId,

					friendship.SenderUserId == userId
						? friendship.Target!.Name
						: friendship.Sender!.Name,

					friendship.SenderUserId == userId
						? friendship.Target!.Phone
						: friendship.Sender!.Phone,

					friendship.Status,

					friendship.SenderUserId == userId))
				.ToArrayAsync();
		}
	}
}
