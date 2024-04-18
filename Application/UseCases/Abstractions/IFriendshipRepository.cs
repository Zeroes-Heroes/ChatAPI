using Application.DTOs.Friends;
using Domain.Entities;
using Domain.Enums;

namespace Application.UseCases.Abstractions
{
	public interface IFriendshipRepository
	{
		Task AddFriendship(int senderUserId, int targetUserId);
		Task<Friendship?> GetFriendshipByUserIds(int[] userIds);
		Task<IEnumerable<FriendDTO>> GetUserFriendships(int userId, FriendshipStatus? status = null);
		Task UpdateFriendshipStatus(int senderUserId, int targetUserId, FriendshipStatus newStatus);
	}
}
