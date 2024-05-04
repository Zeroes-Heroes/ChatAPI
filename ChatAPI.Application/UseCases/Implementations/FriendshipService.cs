using ChatAPI.Application.DTOs.Friends;
using ChatAPI.Application.UseCases.Abstractions;
using ChatAPI.Application.Utilities;
using ChatAPI.Domain.Entities;
using ChatAPI.Domain.Enums;

namespace ChatAPI.Application.UseCases.Implementations
{
	public class FriendshipService(IUserRepository userRepo, IFriendshipRepository friendRepo) : IFriendshipService
	{
		/// <summary>
		/// Creates a friendship between two parties with status 'pending'.
		/// </summary>
		/// <param name="senderUserId">The user id which sends the friend request.</param>
		/// <param name="phone">The receiver's phone number. </param>
		public async Task<Result> AddFriend(int senderUserId, string phone)
		{
			User? targetUser = await userRepo.GetUser(phone);

			if (targetUser is null)
				return Result.Failure("User with that phone is not found.");

			if (senderUserId == targetUser.Id)
				return Result.Failure("You cannot add yourself as a friend.");

			Friendship? friendship = await friendRepo.GetFriendshipByUserIds([senderUserId, targetUser.Id]);

			if (friendship is not null)
				return Result.Failure("You are already friends with that user.");

			await friendRepo.AddFriendship(senderUserId, targetUser.Id);
			return Result.Success();
		}

		/// <summary>
		/// Returns all of the friendships (relationships) of the given user.
		/// </summary>
		/// <param name="userId">The user who's friendships we want to return.</param>
		/// <param name="status">Filter by friendship status.</param>
		/// <param name="isInitiator">Whether or not to filter only these friendships, where the given user id is the sender of the friend request.</param>
		public Task<IEnumerable<FriendDTO>> GetUserFriendships(int userId, FriendshipStatus? status = null, bool? isInitiator = null) =>
			friendRepo.GetUserFriendships(userId, status, isInitiator);

		/// <summary>
		/// Updates the status of a friendship.
		/// </summary>
		/// <param name="senderUserId">The id of the user who sent the friend request.</param>
		/// <param name="targetUserId">The targeted user id (receiver).</param>
		/// <param name="newStatus">The status to update with.</param>
		public Task RespondToFriendRequest(int senderUserId, int targetUserId, FriendshipStatus newStatus) =>
			friendRepo.UpdateFriendshipStatus(senderUserId, targetUserId, newStatus);
	}
}
