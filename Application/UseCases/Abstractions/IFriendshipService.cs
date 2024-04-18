using Application.DTOs.Friends;
using Application.Utilities;
using Domain.Enums;

namespace Application.UseCases.Abstractions
{
	public interface IFriendshipService
	{
		Task<Result> AddFriend(int senderUserId, string targetEmail);
		Task<IEnumerable<FriendDTO>> GetUserFriendships(int userId, FriendshipStatus? status = null);
		Task RespondToFriendRequest(int senderUserId, int targetUserId, FriendshipStatus newStatus);
	}
}
