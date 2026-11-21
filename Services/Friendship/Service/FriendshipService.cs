using System.Net;
using System.Security.Claims;
using Database.Context;
using Database.Entities;
using Database.Enums;
using Microsoft.AspNetCore.SignalR;
using Services.Friendship.Interface;
using Services.Friendship.Models;
using Services.Hubs;
using Services.Hubs.Models;
using Services.NotificationDispatch.Interface;
using Services.Repositories.Friendship.Interface;
using Services.Repositories.User.Interface;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Services.Utilities;
using static Services.Utilities.Statics.LiveEvents;

namespace Services.Friendship.Service;

public class FriendshipService(
	IUserRepository userRepo, IFriendshipRepository friendRepo, IHubContext<BaseHub> hubContext,
	INotificationDispatch notificationDispatcher, AppDbContext dbContext, ISessionTrustChecker trustChecker) : IFriendshipService
{
	private readonly IHubContext<BaseHub> hubContext = hubContext;

	/// <summary>
	/// Creates a friendship between two parties with status 'pending'.
	/// </summary>
	/// <param name="senderUserId">The user id which sends the friend request.</param>
	/// <param name="phone">The receiver's phone number. </param>
	/// <param name="actingUser">See IFriendshipService.AddFriend's own doc comment.</param>
	public async Task<Result<FriendshipDTO>> AddFriend(int senderUserId, string phone, ClaimsPrincipal actingUser)
	{
		UserEntity? targetUser = await userRepo.GetUserNoTracking(phone);

		if (targetUser is null)
			return Result<FriendshipDTO>.Failure("User with that phone is not found.", HttpStatusCode.NotFound);

		if (senderUserId == targetUser.Id)
			return Result<FriendshipDTO>.Failure("You cannot add yourself as a friend.");

		FriendshipEntity? friendship = await friendRepo.GetAnyFriendshipByUserIds([senderUserId, targetUser.Id]);

		if (friendship is not null && friendship.Status != FriendshipStatus.Rejected)
			return Result<FriendshipDTO>.Failure("You already have relations with this user.", HttpStatusCode.Conflict);

		// Re-validated, locked, immediately before the friendship row actually commits - see
		// TrustedMutation's own doc comment; the broadcast to the other party below only runs after.
		TrustCheckResult trust = await TrustedMutation.RunAsync(dbContext, trustChecker, actingUser, () =>
		{
			if (friendship is null)
				friendRepo.AddFriendship(senderUserId, targetUser.Id);
			else
				friendship.Status = FriendshipStatus.Pending;
			return friendRepo.SaveChangesAsync();
		});

		if (!trust.IsTrusted)
		{
			return Result<FriendshipDTO>.Failure("Session is not currently trusted.", HttpStatusCode.Unauthorized);
		}

		UserEntity senderUser = (await userRepo.GetUserNoTracking(senderUserId))!;

		// Send a notification (event) to the targeted user (receiver of the friend request).
		await hubContext.Clients
			.GetUserById(targetUser.Id)
			.SendAsync(
				NewFriendRequest,
				new FriendshipDTO(senderUserId, senderUser.Name, senderUser.Phone, (int)FriendshipStatus.Pending, IsInitiator: false));

		try
		{
			await notificationDispatcher.NotificationForNewFriendshipRequest(targetUser.Id, senderUser.Name);
		}
		catch (Exception ex)
		{
			Console.Write(ex);
		}

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

	private async Task NotifyFriendshipStatusChange(int senderUserId, int targetUserId, FriendshipStatus newStatus)
	{
		try
		{
			switch (newStatus)
			{
				case FriendshipStatus.Accepted:
					await notificationDispatcher.NotificationAcceptFriendship(senderUserId, targetUserId);
					break;
				case FriendshipStatus.Rejected:
					await notificationDispatcher.NotificationForRejectedFriendship(senderUserId, targetUserId);
					break;
				case FriendshipStatus.Blocked:
					await notificationDispatcher.NotificationForBlockedFriendship(senderUserId, targetUserId);
					break;
			}
		}
		catch (Exception ex)
		{
			Console.Write(ex);
		}
	}

	/// <summary>
	/// Updates the status of a friendship.
	/// </summary>
	/// <param name="senderUserId">The id of the user who sent the friend request.</param>
	/// <param name="targetUserId">The targeted user id (receiver).</param>
	/// <param name="newStatus">The status to update with.</param>
	public async Task<Result> RespondToFriendRequest(int senderUserId, int targetUserId, FriendshipStatus newStatus, ClaimsPrincipal actingUser)
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

		// Re-validated, locked, immediately before the status change actually commits - see
		// TrustedMutation's own doc comment; the broadcast to the original sender below only runs after.
		TrustCheckResult trust = await TrustedMutation.RunAsync(dbContext, trustChecker, actingUser, () =>
		{
			friendship.Status = newStatus;
			return friendRepo.SaveChangesAsync();
		});

		if (!trust.IsTrusted)
		{
			return Result.Failure("Session is not currently trusted.", HttpStatusCode.Unauthorized);
		}

		await NotifyFriendshipStatusChange(senderUserId, targetUserId, newStatus);

		await hubContext.Clients.GetUserById(senderUserId).SendAsync(FriendRequestAnswer, new FriendRequestModelAnswerModel(targetUserId, (int)newStatus));

		return Result.Success();
	}
}
