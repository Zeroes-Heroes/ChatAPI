using Database.Enums;
using Services.Friendship.Models;
using Services.Utilities;

namespace Services.Friendship.Interface;

public interface IFriendshipService
{
	Task<Result<FriendshipDTO>> AddFriend(int senderUserId, string phone);
	Task<IEnumerable<FriendshipDTO>> GetUserFriendships(int userId, FriendshipStatus? status = null, bool? isInitiator = null);
	Task<Result> RespondToFriendRequest(int senderUserId, int targetUserId, FriendshipStatus newStatus);
}
