using Database.Context;
using Database.Entities;
using Database.Enums;
using Microsoft.EntityFrameworkCore;
using Services.Friendship.Models;
using Services.Repositories.Friendship.Interface;

namespace Services.Repositories.Friendship.Repository;

public class FriendshipRepository(AppDbContext dbContext) : IFriendshipRepository
{
	public void AddFriendship(int senderUserId, int targetUserId)
	{
		FriendshipEntity friendship = new(senderUserId, targetUserId);
		dbContext.Friendships.Add(friendship);
	}

	public Task<FriendshipEntity?> GetSpecificFriendshipByUserIds(int senderUserId, int targetUserId) =>
		dbContext.Friendships
			.Where(f => f.SenderUserId == senderUserId
					 && f.TargetUserId == targetUserId)
			.FirstOrDefaultAsync();

	/// <summary>
	/// Gets any friendship for the two given user id's
	/// regardless who is the sender and who is the target.
	/// </summary>
	/// <param name="userIds">Sender and target user ids</param>
	public Task<FriendshipEntity?> GetAnyFriendshipByUserIds(params int[] userIds) =>
		dbContext.Friendships
			.Where(f => userIds.Contains(f.SenderUserId)
					 && userIds.Contains(f.TargetUserId))
			.FirstOrDefaultAsync();

	public async Task<IReadOnlyList<FriendshipRelationRow>> GetUserRelations(int userId) =>
		await dbContext.Friendships.AsNoTracking()
			.Where(f => f.SenderUserId == userId || f.TargetUserId == userId)
			.Select(friendship => new FriendshipRelationRow(

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

	public Task SaveChangesAsync() =>
		dbContext.SaveChangesAsync();

	public async Task<bool> AreUsersFriends(int[] userIds) =>
	(await dbContext.Friendships
			.Where(f => userIds.Contains(f.SenderUserId)
					 && userIds.Contains(f.TargetUserId))
			.FirstOrDefaultAsync())?.Status == FriendshipStatus.Accepted;
}
