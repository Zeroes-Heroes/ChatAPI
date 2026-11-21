using System.Net;
using System.Security.Claims;
using Database.Context;
using Database.Entities;
using Database.Enums;
using Microsoft.AspNetCore.SignalR;
using Services.Contacts.Interface;
using Services.Contacts.Models;
using Services.Contacts.Service;
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
	INotificationDispatch notificationDispatcher, AppDbContext dbContext, ISessionTrustChecker trustChecker,
	IPersonalRelationDirectory relations, IPersonalRelationService personalRelations, IProfileVisibility profiles) : IFriendshipService
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

		// Stage 09: "блокираният не може да добавя блокиралия го". Checked here as well as at the commit
		// below, so a blocked party gets the refusal instead of a half-written request; the authoritative
		// re-check is inside the transaction.
		PersonalBlockPair blocks = await relations.BlocksBetweenAsync(senderUserId, targetUser.Id);
		if (blocks.AnyBlock)
			return Result<FriendshipDTO>.Failure("You cannot add this contact.", HttpStatusCode.Forbidden);

		FriendshipEntity? friendship = await friendRepo.GetAnyFriendshipByUserIds([senderUserId, targetUser.Id]);

		if (friendship is not null && friendship.Status != FriendshipStatus.Rejected)
			return Result<FriendshipDTO>.Failure("You already have relations with this user.", HttpStatusCode.Conflict);

		// Re-validated, locked, immediately before the friendship row actually commits - see
		// TrustedMutation's own doc comment; the broadcast to the other party below only runs after.
		bool refusedByBlock = false;
		TrustCheckResult trust = await TrustedMutation.RunAsync(dbContext, trustChecker, actingUser, async () =>
		{
			// Re-read inside the transaction: a block committed between the check above and here must stop
			// the request, not merely have been absent a moment ago.
			PersonalBlockPair current = await relations.BlocksBetweenAsync(senderUserId, targetUser.Id);
			if (current.AnyBlock)
			{
				refusedByBlock = true;
				return;
			}

			if (friendship is null)
				friendRepo.AddFriendship(senderUserId, targetUser.Id);
			else
				friendship.Status = FriendshipStatus.Pending;
			await friendRepo.SaveChangesAsync();
		});

		if (!trust.IsTrusted)
		{
			return Result<FriendshipDTO>.Failure("Session is not currently trusted.", HttpStatusCode.Unauthorized);
		}

		if (refusedByBlock)
		{
			return Result<FriendshipDTO>.Failure("You cannot add this contact.", HttpStatusCode.Forbidden);
		}

		UserEntity senderUser = (await userRepo.GetUserNoTracking(senderUserId))!;

		// Stage 09: even here the identity goes through the one resolver, from the RECIPIENT's point of
		// view, so the live event cannot disclose more than GET /friends would. A pending request is a
		// disclosing relation (its recipient has to know who is asking), so this normally resolves to the
		// sender's own name and phone - but it is the resolver that decides, not this call site.
		ProfileIdentity senderAsSeenByTarget = await profiles.ResolveAsync(targetUser.Id, senderUserId);

		// Send a notification (event) to the targeted user (receiver of the friend request).
		await hubContext.Clients
			.GetUserById(targetUser.Id)
			.SendAsync(
				NewFriendRequest,
				new FriendshipDTO(
					senderUserId, senderAsSeenByTarget.Name, senderAsSeenByTarget.Phone,
					(int)FriendshipStatus.Pending, IsInitiator: false, senderAsSeenByTarget.IsKnown,
					BlockedByMe: false, CorrespondenceSuspended: true));

		try
		{
			await notificationDispatcher.NotificationForNewFriendshipRequest(targetUser.Id, senderAsSeenByTarget.Name);
		}
		catch (Exception ex)
		{
			Console.Write(ex);
		}

		ProfileIdentity targetAsSeenBySender = await profiles.ResolveAsync(senderUserId, targetUser.Id);

		return Result<FriendshipDTO>.Success(
			new FriendshipDTO(
				targetUser.Id, targetAsSeenBySender.Name, targetAsSeenBySender.Phone,
				(int)FriendshipStatus.Pending, IsInitiator: true, targetAsSeenBySender.IsKnown,
				BlockedByMe: false, CorrespondenceSuspended: true));
	}

	/// <summary>
	/// Returns all of the friendships (relationships) of the given user.
	/// </summary>
	/// <param name="userId">The user who's friendships we want to return.</param>
	/// <param name="status">Filter by friendship status.</param>
	/// <param name="isInitiator">Whether or not to filter only these friendships, where the given user id is the sender of the friend request.</param>
	/// <summary>
	/// Stage 09: the list a client actually sees. Three things happen here that cannot happen in the SQL
	/// that reads the rows:
	/// <list type="number">
	/// <item>a block is projected OVER the legacy status (and over the initiator flag), so the blocker -
	/// and only the blocker - finds the pair in its own blocked list and can lift it;</item>
	/// <item>a block with no friendship row behind it (someone met in a group) is listed too, because an
	/// unlistable block could never be lifted;</item>
	/// <item>every name/phone goes through <see cref="IProfileVisibility"/>, so a party with no
	/// disclosing relation reads <c>unknown</c> with no phone.</item>
	/// </list>
	/// The caller's status/role filters are applied AFTER all of that, to what is displayed.
	/// </summary>
	public async Task<IEnumerable<FriendshipDTO>> GetUserFriendships(int userId, FriendshipStatus? status = null, bool? isInitiator = null)
	{
		IReadOnlyList<FriendshipRelationRow> rows = await friendRepo.GetUserRelations(userId);
		IReadOnlyDictionary<int, PersonalBlockPair> blocks = await relations.AllBlocksAsync(userId);

		int[] everyone = [.. rows.Select(r => r.UserId).Concat(blocks.Keys).Distinct()];
		if (everyone.Length == 0)
		{
			return [];
		}

		IReadOnlyDictionary<int, ProfileIdentity> identities = await profiles.ResolveAsync(userId, everyone);
		IReadOnlySet<int> suspended = await relations.SuspendedCorrespondentsAsync(userId, everyone);
		Dictionary<int, FriendshipRelationRow> rowOf = rows
			.GroupBy(r => r.UserId)
			.ToDictionary(g => g.Key, g => g.First());

		List<FriendshipDTO> projected = [];
		foreach (int otherId in everyone)
		{
			rowOf.TryGetValue(otherId, out FriendshipRelationRow? row);
			PersonalBlockPair pair = blocks.TryGetValue(otherId, out PersonalBlockPair p) ? p : PersonalBlockPair.None;
			ProfileIdentity identity = identities.TryGetValue(otherId, out ProfileIdentity? i) ? i : ProfileIdentity.Unknown(otherId);

			PersonalRelationChange view = PersonalRelationProjection.Project(
				otherId, identity, row?.Status, row?.IsInitiator ?? false, pair, suspended.Contains(otherId));

			projected.Add(new FriendshipDTO(
				view.UserId, view.Profile.Name, view.Profile.Phone, view.FriendshipStatus, view.IsInitiator,
				view.Profile.IsKnown, view.BlockedByMe, view.CorrespondenceSuspended));
		}

		IEnumerable<FriendshipDTO> result = projected;
		if (status.HasValue)
		{
			result = result.Where(f => f.FriendshipStatus == (int)status.Value);
		}

		if (isInitiator.HasValue)
		{
			result = result.Where(f => f.IsInitiator == isInitiator.Value);
		}

		return [.. result];
	}

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

				// Blocked never reaches here: RespondToFriendRequest delegates it to
				// IPersonalRelationService, which owns the write, both live copies AND the push - so the
				// two entrypoints that can block (this one and POST /friends/block) behave identically.
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
		// Stage 09: blocking is no longer a legacy status write. It is a directional record with an
		// explicit blocker (Services/Contacts), so that the request's SENDER can block too and so that
		// lifting it restores the relation it overlays instead of guessing a previous status. This
		// endpoint stays the blocked-from-a-request entrypoint the existing client already uses; the rule,
		// the write, the trust re-check and both live copies all live in the one writer.
		if (newStatus is FriendshipStatus.Blocked)
		{
			Result<PersonalRelationChange> blocked = await personalRelations.BlockAsync(actingUser, senderUserId);
			return blocked.IsSuccess
				? Result.Success()
				: Result.Failure(blocked.Error, (HttpStatusCode)blocked.StatusCode);
		}

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
