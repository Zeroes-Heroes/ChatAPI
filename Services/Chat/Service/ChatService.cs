using System.Net;
using System.Security.Claims;
using Database.Context;
using Database.Entities;
using Microsoft.AspNetCore.SignalR;
using Services.Chat.Interface;
using Services.Chat.Models.CreateChat;
using Services.Chat.Models.Events;
using Services.Chat.Models.GetChats;
using Services.Hubs;
using Services.Membership;
using Services.Membership.Interface;
using Services.Membership.Models;
using Services.NotificationDispatch.Interface;
using Services.Repositories.Chat.Interface;
using Services.Repositories.Friendship.Interface;
using Services.Repositories.User.Interface;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Services.Utilities;
using static Services.Utilities.Statics.LiveEvents;

namespace Services.Chat.Service;

internal class ChatService(
	IChatRepository chatRepository, IUserRepository userRepository, IFriendshipRepository friendshipRepository,
	IHubContext<BaseHub> hubContext, INotificationDispatch notificationDispatch,
	AppDbContext dbContext, ISessionTrustChecker trustChecker,
	IChatMembershipService membershipService, IChatAccessAuthority accessAuthority) : IChatService
{
	public async Task<Result> CreateChat(CreateChatRequest createChatRequest, int chatCreatorId, ClaimsPrincipal actingUser)
	{
		// One canonical participant set for the whole creation: the two-friends rule, the duplicate and
		// blocking guard, the chat's persisted kind and the notification audience are all decided over
		// THIS set. Keying any of them on the raw request instead lets a repeated id ("me, you, you")
		// skip the friendship check while still producing - and durably persisting - a personal chat
		// between exactly two people (plan/07-membership.md: "личен чат между двама приятели").
		int[] participants = [.. createChatRequest.UserIds.Distinct()];

		UserEntity[] userEntities = await userRepository.GetUsers(participants);

		Result? validationResult = await ValidateCreateChatRequest(participants, chatCreatorId, userEntities);

		if (validationResult is not null)
			return validationResult;

		// Re-validated (session/device/trust-version), locked, immediately before the insert actually
		// commits - the controller's own [Authorize] already ran once, on request entry, but a concurrent
		// revoke of this exact caller can still commit in the gap between that check and this mutation; see
		// TrustedMutation's own doc comment. The broadcast below only ever runs after a successful commit.
		//
		// Stage 07: the membership rules that must hold AT the insert - no existing chat with exactly this
		// active participant set, and nobody being invited has blocked the creator - are checked inside this
		// same transaction, under the participant-set advisory lock, and the sync chat/membership rows are
		// projected here too, so the chat and its membership commit together or not at all.
		ChatEntity? chatEntity = null;
		TrustCheckResult trust;
		try
		{
			trust = await TrustedMutation.RunAsync(dbContext, trustChecker, actingUser, async () =>
			{
				await membershipService.GuardChatCreationAsync(chatCreatorId, participants);
				chatEntity = await chatRepository.AddChat(new ChatEntity(createChatRequest.ChatName, userEntities));
				await membershipService.ProjectCreatedChatAsync(chatEntity.Id, chatEntity.Name, participants);
			});
		}
		catch (ChatMembershipRefusedException ex)
		{
			// Rolled back with nothing written - the refusal escaped before TrustedMutation's commit.
			return Result.Failure(ex.Message, ex.Status);
		}

		if (!trust.IsTrusted)
		{
			return Result.Failure("Session is not currently trusted.", HttpStatusCode.Unauthorized);
		}

		ChatCreatedEvent chatCreatedEvent = new(chatEntity!.Id, chatEntity.Name, chatEntity.Users.Select(u => u.Id).ToArray());
		SendEventChatCreated(chatCreatedEvent);

		try
		{
			await notificationDispatch.NotificationForNewChat(participants, chatCreatorId, chatEntity.Id);
		}
		catch (Exception ex)
		{
			Console.Write(ex);
		}

		return Result.Success();
	}

	private async Task<Result?> ValidateCreateChatRequest(int[] participants, int chatCreatorId, UserEntity[] userEntities)
	{
		if (participants.Length < 2)
			return Result.Failure("Can't create a chat with less than two users.", HttpStatusCode.BadRequest);

		// Every requested participant must be a real user, because the membership projection provisions an
		// account context per participant: an id with no Users row would otherwise fail that insert's
		// foreign key in the middle of the creation transaction instead of being refused here. Same answer
		// IChatMembershipService.AddMembersAsync already gives for an unknown participant.
		if (userEntities.Length != participants.Length)
			return Result.Failure("Unknown participant.", HttpStatusCode.NotFound);

		// plan/07-membership.md: the creator is one of the participants of the chat it creates - a personal
		// chat for two friends, a group for more than two. A request that does not contain its own creator
		// would otherwise produce a chat with a membership the caller itself is not part of.
		if (!participants.Contains(chatCreatorId))
			return Result.Failure("The chat's participants must include you.", HttpStatusCode.BadRequest);

		bool isTwoPeopleChat = participants.Length == 2;

		if (isTwoPeopleChat)
		{
			bool areUsersFriends = await friendshipRepository.AreUsersFriends(participants);

			if (!areUsersFriends)
				return Result.Failure("Chats can only be created with friends.", HttpStatusCode.BadRequest);
		}

		// The authoritative duplicate check is IChatMembershipService.GuardChatCreationAsync, under the
		// participant-set lock inside the write transaction (the legacy chatRepository.DoesChatExist query
		// answers a different question now: the legacy row keeps every PAST participant, so it would reject
		// a set that is no longer anyone's active composition).
		return null;
	}

	public async Task<Result<GetChatsResponse[]>> GetChats(int userId, ClaimsPrincipal actingUser)
	{
		bool doesUserExist = await userRepository.DoesUserExist(userId);

		if (!doesUserExist)
			return Result<GetChatsResponse[]>.Failure("Unable to get chat's for the user.", HttpStatusCode.BadRequest);

		ChatEntity[] chats = await chatRepository.GetChats(userId);
		int[] chatIds = [.. chats.Select(c => c.Id)];

		// One shared check for the whole list (stage 07): it answers None both for a chat belonging to a
		// superseded account context (the stage-06 exclusion, which it composes) and for a chat this
		// context never joined, and Frozen for one it only holds as a read-only archive.
		IReadOnlyDictionary<int, ChatAccess> access = await accessAuthority.GetManyAsync(actingUser, chatIds);
		IReadOnlyDictionary<int, ChatMembershipView> views = await membershipService.DescribeAsync(actingUser, chatIds);

		List<GetChatsResponse> chatsResponse = [];
		foreach (ChatEntity chat in chats)
		{
			if (!access.TryGetValue(chat.Id, out ChatAccess? chatAccess) || !chatAccess.CanRead)
			{
				continue;
			}

			// A chat created before stage 07 has no membership projection yet; its legacy participant list
			// is its composition and it is nobody's archive, exactly as before.
			if (!views.TryGetValue(chat.Id, out ChatMembershipView? view))
			{
				view = new ChatMembershipView(chat.Users.Count > 2, IsArchived: false, [.. chat.Users.Select(u => u.Id)]);
			}

			// Stage 09: the same authority answers whether new correspondence is possible, so the list and
			// the hub's send-message refusal can never disagree about one chat.
			chatsResponse.Add(new GetChatsResponse(
				chat.Id, chat.Name!, [.. view.MemberLegacyUserIds], view.IsGroup, view.IsArchived, chatAccess.CanSendNewMessages));
		}

		return Result<GetChatsResponse[]>.Success([.. chatsResponse]);
	}

	public void SendEventChatCreated(ChatCreatedEvent chatCreatedEvent)
	{
		hubContext.Clients.GetUsersByIds(chatCreatedEvent.UserIds).SendAsync(ChatCreated, chatCreatedEvent);
	}
}
