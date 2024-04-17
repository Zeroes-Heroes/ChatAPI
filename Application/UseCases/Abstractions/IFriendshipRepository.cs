using Application.DTOs.Friends;
using Domain.Entities;

namespace Application.UseCases.Abstractions
{
	public interface IFriendshipRepository
	{
		Task AddFriendship(int senderUserId, int targetUserId);
		Task<Friendship?> GetFriendshipByUserIds(int[] userIds);
		Task<IEnumerable<FriendDTO>> GetUserFriendships(int userId);
	}
}
