using Application.DTOs.Friends;
using Application.UseCases.Abstractions;
using Application.Utilities;
using Domain.Entities;

namespace Application.UseCases.Implementations
{
	public class FriendshipService(IUserRepository userRepo, IFriendshipRepository friendRepo) : IFriendshipService
	{
		public async Task<Result> AddFriend(int senderUserId, string targetEmail)
		{
			User? targetUser = await userRepo.GetUserByEmail(targetEmail, trackEntity: false);

			if (targetUser is null)
				return Result.Failure("User with that email is not found.");

			if (senderUserId == targetUser.Id)
				return Result.Failure("You cannot add yourself as a friend.");

			Friendship? friendship = await friendRepo.GetFriendshipByUserIds([senderUserId, targetUser.Id]);

			if (friendship is not null)
				return Result.Failure("You are already friends with that user.");

			await friendRepo.AddFriendship(senderUserId, targetUser.Id);
			return Result.Success();
		}

		public Task<IEnumerable<FriendDTO>> GetUserFriendships(int userId) =>
			friendRepo.GetUserFriendships(userId);
	}
}
