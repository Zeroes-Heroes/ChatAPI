using ChatAPI.Application.DTOs.Friends;
using ChatAPI.Application.DTOs.Friends.LiveEvents;
using ChatAPI.Application.Hubs;
using ChatAPI.Application.UseCases.Abstractions.Repositories;
using ChatAPI.Application.UseCases.Abstractions.Services;
using ChatAPI.Application.Utilities;
using ChatAPI.Domain.Entities;
using ChatAPI.Domain.Enums;
using Microsoft.AspNetCore.SignalR;
using System.Net;
using static ChatAPI.Domain.LiveConnections.LiveEvents;

namespace ChatAPI.Application.UseCases.Implementations
{
	public class FriendshipService(IUserRepository userRepo, IFriendshipRepository friendRepo, IHubContext<BaseHub> hubContext) : IFriendshipService
	{
		private readonly IHubContext<BaseHub> hubContext = hubContext;

		/// <summary>
		/// Creates a friendship between two parties with status 'pending'.
		/// </summary>
		/// <param name="senderUserId">The user id which sends the friend request.</param>
		/// <param name="phone">The receiver's phone number. </param>
		public async Task<Result> AddFriend(int senderUserId, string phone)
		{
			User? targetUser = await userRepo.GetUserNoTracking(phone);

			if (targetUser is null)
				return Result.Failure("User with that phone is not found.", HttpStatusCode.NotFound);

			if (senderUserId == targetUser.Id)
				return Result.Failure("You cannot add yourself as a friend.");

			Friendship? friendship = await friendRepo.GetAnyFriendshipByUserIds([senderUserId, targetUser.Id]);

			if (friendship is not null)
				return Result.Failure("You already have relations with this user.", HttpStatusCode.Conflict);

			friendRepo.AddFriendship(senderUserId, targetUser.Id);
			await friendRepo.SaveChangesAsync();

			User senderUser = (await userRepo.GetUserNoTracking(senderUserId))!;

			// Send a notification (event) to the targeted user (receiver of the friend request).
			await hubContext.Clients
				.User(targetUser.Id.ToString())
				.SendAsync(
					NewFriendRequest,
					new FriendRequestModel(senderUserId, senderUser.Phone, senderUser.Name));

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
		public async Task<Result> RespondToFriendRequest(int senderUserId, int targetUserId, FriendshipStatus newStatus)
		{
			Friendship? friendship = await friendRepo.GetSpecificFriendshipByUserIds(senderUserId, targetUserId);

			if (friendship is null)
				return Result.Failure("Could not find a friend request.", HttpStatusCode.NotFound);

			if (newStatus is FriendshipStatus.Pending)
				return Result.Failure("You cannot change the status to pending.", HttpStatusCode.Forbidden);

			/******** [CHAT-23] History of Friendship updates ********/

			//if (friendship.Status is FriendshipStatus.Blocked or FriendshipStatus.Rejected &&
			//	friendship.ModifiedBy == senderUserId)
			//	return Result.Failure("You cannot change the status.", HttpStatusCode.Forbidden);

			friendship.Status = newStatus;
			await friendRepo.SaveChangesAsync();

			await hubContext.Clients.User(targetUserId.ToString()).SendAsync(FriendRequestAnswer, senderUserId, newStatus); ;

			return Result.Success();
		}
	}
}
