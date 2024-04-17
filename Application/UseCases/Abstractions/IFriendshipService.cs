using Application.DTOs.Friends;
using Application.Utilities;

namespace Application.UseCases.Abstractions
{
	public interface IFriendshipService
	{
		Task<Result> AddFriend(int senderUserId, string targetEmail);
		Task<IEnumerable<FriendDTO>> GetUserFriendships(int userId);
	}
}
