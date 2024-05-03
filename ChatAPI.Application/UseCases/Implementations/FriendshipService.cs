using ChatAPI.Application.DTOs.Friends;
using ChatAPI.Application.UseCases.Abstractions;
using ChatAPI.Application.Utilities;
using ChatAPI.Domain.Entities;
using ChatAPI.Domain.Enums;

namespace ChatAPI.Application.UseCases.Implementations
{
	public class FriendshipService(IUserRepository userRepo, IFriendshipRepository friendRepo) : IFriendshipService
	{
		public async Task<Result> AddFriend(int senderUserId, string targetEmail)
		{
			//User? targetUser = await userRepo.GetUserByEmailAsNoTracking(targetEmail);

			//if (targetUser is null)
			//	return Result.Failure("User with that email is not found.");

			//if (senderUserId == targetUser.Id)
			//	return Result.Failure("You cannot add yourself as a friend.");

			//Friendship? friendship = await friendRepo.GetFriendshipByUserIds([senderUserId, targetUser.Id]);

			//if (friendship is not null)
			//	return Result.Failure("You are already friends with that user.");

			//await friendRepo.AddFriendship(senderUserId, targetUser.Id);
			//return Result.Success();
		}

		public Task<IEnumerable<FriendDTO>> GetUserFriendships(int userId, FriendshipStatus? status = null, bool? isInitiator = null) =>
			friendRepo.GetUserFriendships(userId, status, isInitiator);

		public Task RespondToFriendRequest(int senderUserId, int targetUserId, FriendshipStatus newStatus) =>
			friendRepo.UpdateFriendshipStatus(senderUserId, targetUserId, newStatus);
	}
}
