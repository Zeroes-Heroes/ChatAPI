using ChatAPI.Application.DTOs.Friends;
using ChatAPI.Domain.Entities;
using ChatAPI.Domain.Enums;

namespace ChatAPI.Application.UseCases.Abstractions
{
	public interface IFriendshipRepository
	{
		Task AddFriendship(int senderUserId, int targetUserId);
		Task<Friendship?> GetFriendshipByUserIds(int[] userIds);
		Task<IEnumerable<FriendDTO>> GetUserFriendships(int userId, FriendshipStatus? status = null, bool? isInitiator = null);
		Task UpdateFriendshipStatus(int senderUserId, int targetUserId, FriendshipStatus newStatus);
	}
}
