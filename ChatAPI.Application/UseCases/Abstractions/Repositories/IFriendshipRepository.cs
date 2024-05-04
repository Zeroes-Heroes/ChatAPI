using ChatAPI.Application.DTOs.Friends;
using ChatAPI.Domain.Entities;
using ChatAPI.Domain.Enums;

namespace ChatAPI.Application.UseCases.Abstractions.Repositories
{
    public interface IFriendshipRepository : IBaseRepository
    {
        void AddFriendship(int senderUserId, int targetUserId);
        Task<Friendship?> GetSpecificFriendshipByUserIds(int senderUserId, int targetUserId);
		Task<Friendship?> GetAnyFriendshipByUserIds(params int[] userIds);
        Task<IEnumerable<FriendDTO>> GetUserFriendships(int userId, FriendshipStatus? status = null, bool? isInitiator = null);
    }
}
