using Database.Entities;
using Database.Enums;
using Microsoft.AspNetCore.SignalR;
using Services.Friendship.Interface;
using Services.Friendship.Models;
using Services.Hubs;
using Services.Hubs.Models;
using Services.PushNotification.Interface;
using Services.Repositories.Friendship.Interface;
using Services.Repositories.User.Interface;
using Services.Utilities;
using System.Net;
using static Services.Utilities.Statics.LiveEvents;

namespace Services.Friendship.Service;

public class FriendshipService(IUserRepository userRepo, IFriendshipRepository friendRepo, IHubContext<BaseHub> hubContext, IPushNotification pushNotification) : IFriendshipService
{
	private readonly IHubContext<BaseHub> hubContext = hubContext;

	/// <summary>
	/// Creates a friendship between two parties with status 'pending'.
	/// </summary>
	/// <param name="senderUserId">The user id which sends the friend request.</param>
	/// <param name="phone">The receiver's phone number. </param>
	public async Task<Result<FriendshipDTO>> AddFriend(int senderUserId, string phone)
	{
		UserEntity? targetUser = await userRepo.GetUserNoTracking(phone);

		if (targetUser is null)
			return Result<FriendshipDTO>.Failure("User with that phone is not found.", HttpStatusCode.NotFound);

		if (senderUserId == targetUser.Id)
			return Result<FriendshipDTO>.Failure("You cannot add yourself as a friend.");

		FriendshipEntity? friendship = await friendRepo.GetAnyFriendshipByUserIds([senderUserId, targetUser.Id]);

		if (friendship is null)
			friendRepo.AddFriendship(senderUserId, targetUser.Id);
		else if (friendship.Status == FriendshipStatus.Rejected)
			friendship.Status = FriendshipStatus.Pending;
		else
			return Result<FriendshipDTO>.Failure("You already have relations with this user.", HttpStatusCode.Conflict);

		await friendRepo.SaveChangesAsync();

		UserEntity senderUser = (await userRepo.GetUserNoTracking(senderUserId))!;

		// Send a notification (event) to the targeted user (receiver of the friend request).
		await hubContext.Clients
			.GetUserById(targetUser.Id)
			.SendAsync(
				NewFriendRequest,
				new FriendshipDTO(senderUserId, senderUser.Name, senderUser.Phone, (int)FriendshipStatus.Pending, IsInitiator: false));

		pushNotification.NotificationForNewFriendshipRequest(targetUser.Id, senderUser.Name);

		return Result<FriendshipDTO>.Success(
			new FriendshipDTO(targetUser.Id, targetUser.Name, targetUser.Phone, (int)FriendshipStatus.Pending, IsInitiator: true));
	}

	/// <summary>
	/// Returns all of the friendships (relationships) of the given user.
	/// </summary>
	/// <param name="userId">The user who's friendships we want to return.</param>
	/// <param name="status">Filter by friendship status.</param>
	/// <param name="isInitiator">Whether or not to filter only these friendships, where the given user id is the sender of the friend request.</param>
	public Task<IEnumerable<FriendshipDTO>> GetUserFriendships(int userId, FriendshipStatus? status = null, bool? isInitiator = null) =>
		friendRepo.GetUserFriendships(userId, status, isInitiator);

	/// <summary>
	/// Updates the status of a friendship.
	/// </summary>
	/// <param name="senderUserId">The id of the user who sent the friend request.</param>
	/// <param name="targetUserId">The targeted user id (receiver).</param>
	/// <param name="newStatus">The status to update with.</param>
	public async Task<Result> RespondToFriendRequest(int senderUserId, int targetUserId, FriendshipStatus newStatus)
	{
		FriendshipEntity? friendship = await friendRepo.GetSpecificFriendshipByUserIds(senderUserId, targetUserId);

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

		switch (newStatus)
		{
			case FriendshipStatus.Accepted:
				pushNotification.NotificationForAcceptFriendship(senderUserId, targetUserId);
				break;
			case FriendshipStatus.Rejected:
				pushNotification.NotificationForRejectedFriendship(senderUserId, targetUserId);
				break;
			case FriendshipStatus.Blocked:
				pushNotification.NotificationForBlockedFriendship(senderUserId, targetUserId);
				break;
		}

		await hubContext.Clients.GetUserById(senderUserId).SendAsync(FriendRequestAnswer, new FriendRequestModelAnswerModel(targetUserId, (int)newStatus));

		return Result.Success();
	}
}
