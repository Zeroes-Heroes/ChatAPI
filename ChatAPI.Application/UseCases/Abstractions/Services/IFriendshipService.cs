using ChatAPI.Application.DTOs.Friends;
using ChatAPI.Application.Utilities;
using ChatAPI.Domain.Enums;

namespace ChatAPI.Application.UseCases.Abstractions.Services
{
    public interface IFriendshipService
    {
        Task<Result> AddFriend(int senderUserId, string phone);
        Task<IEnumerable<FriendshipDTO>> GetUserFriendships(int userId, FriendshipStatus? status = null, bool? isInitiator = null);
        Task<Result> RespondToFriendRequest(int senderUserId, int targetUserId, FriendshipStatus newStatus);
    }
}
