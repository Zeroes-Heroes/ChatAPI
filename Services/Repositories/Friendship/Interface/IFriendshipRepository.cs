using Database.Entities;
using Database.Enums;
using Services.Friendship.Models;
using Services.Repositories.Base.Interface;

namespace Services.Repositories.Friendship.Interface;

public interface IFriendshipRepository : IBaseRepository
{
	void AddFriendship(int senderUserId, int targetUserId);
	Task<FriendshipEntity?> GetSpecificFriendshipByUserIds(int senderUserId, int targetUserId);
	Task<FriendshipEntity?> GetAnyFriendshipByUserIds(params int[] userIds);
	Task<IEnumerable<FriendshipDTO>> GetUserFriendships(int userId, FriendshipStatus? status = null, bool? isInitiator = null);
	Task<bool> AreUsersFriends(int[] userIds);
}
