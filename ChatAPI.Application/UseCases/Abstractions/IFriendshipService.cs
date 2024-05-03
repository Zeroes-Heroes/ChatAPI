using ChatAPI.Application.DTOs.Friends;
using ChatAPI.Application.Utilities;
using ChatAPI.Domain.Enums;

namespace ChatAPI.Application.UseCases.Abstractions
{
	public interface IFriendshipService
	{
		Task<Result> AddFriend(int senderUserId, string phone);
		Task<IEnumerable<FriendDTO>> GetUserFriendships(int userId, FriendshipStatus? status = null, bool? isInitiator = null);
		Task RespondToFriendRequest(int senderUserId, int targetUserId, FriendshipStatus newStatus);
	}
}
