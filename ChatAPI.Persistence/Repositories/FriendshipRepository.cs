using ChatAPI.Application.DTOs.Friends;
using ChatAPI.Application.UseCases.Abstractions.Repositories;
using ChatAPI.Domain.Entities;
using ChatAPI.Domain.Enums;
using ChatAPI.Persistence.Database;
using Microsoft.EntityFrameworkCore;

namespace ChatAPI.Persistence.Repositories
{
	public class FriendshipRepository(AppDbContext dbContext) : IFriendshipRepository
	{
		public void AddFriendship(int senderUserId, int targetUserId)
		{
			Friendship friendship = new(senderUserId, targetUserId);
			dbContext.Friendships.Add(friendship);
		}

		public Task<Friendship?> GetSpecificFriendshipByUserIds(int senderUserId, int targetUserId) =>
			dbContext.Friendships
				.Where(f => f.SenderUserId == senderUserId
						 && f.TargetUserId == targetUserId)
				.FirstOrDefaultAsync();

		/// <summary>
		/// Gets any friendship for the two given user id's
		/// regardless who is the sender and who is the target.
		/// </summary>
		/// <param name="userIds">Sender and target user ids</param>
		public Task<Friendship?> GetAnyFriendshipByUserIds(params int[] userIds) =>
			dbContext.Friendships
				.Where(f => userIds.Contains(f.SenderUserId)
						 && userIds.Contains(f.TargetUserId))
				.FirstOrDefaultAsync();

		public async Task<IEnumerable<FriendshipDTO>> GetUserFriendships(int userId, FriendshipStatus? status = null, bool? isInitiator = null)
		{
			IQueryable<Friendship> query = dbContext.Friendships
				.Where(f => f.SenderUserId == userId || f.TargetUserId == userId);

			if (status.HasValue)
				query = query.Where(f => f.Status == status);

			if (isInitiator.HasValue)
				query = query.Where(f => isInitiator.Value
								? f.SenderUserId == userId
								: f.TargetUserId == userId);

			return await query.Select(friendship => new FriendshipDTO(

					friendship.SenderUserId == userId
						? friendship.TargetUserId
						: friendship.SenderUserId,

					friendship.SenderUserId == userId
						? friendship.Target!.Name
						: friendship.Sender!.Name,

					friendship.SenderUserId == userId
						? friendship.Target!.Phone
						: friendship.Sender!.Phone,

					(int)friendship.Status,

					friendship.SenderUserId == userId))
				.ToArrayAsync();
		}

		public Task SaveChangesAsync() =>
			dbContext.SaveChangesAsync();
	}
}
