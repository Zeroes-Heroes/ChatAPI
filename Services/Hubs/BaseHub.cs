using Database.Context;
using Database.Entities;
using Database.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Services.Extensions;
using Services.Hubs.Models;
using Services.Membership.Interface;
using Services.Membership.Models;
using Services.NotificationDispatch.Interface;
using Services.Presence.Interface;
using Services.Sessions;
using Services.Sessions.Interface;
using Services.Sessions.Models;
using Services.Sessions.Service;
using Services.Utilities.Statics;
using System.Security.Claims;

namespace Services.Hubs;

/*
Important things to note when working with SignalR Hubs:

Hubs are transient!
	* Don't store state in a property of the hub class.
	
	* Each hub method call is executed on a new hub instance.
	
	* Don't instantiate a hub directly via dependency injection.
		To send messages to a client from elsewhere in your application use
		IHubContext<T> : where T is Hub (ref. FriendshipService).

	* Use await when calling asynchronous methods that depend on the hub staying alive.
		For example, a method such as Clients.All.SendAsync(...) can fail if it's called without await
		and the hub method completes before SendAsync finishes.

Other things to note:
	- By default, SignalR uses the ClaimTypes.NameIdentifier from the ClaimsPrincipal associated 
	with the connection as the user identifier.

	- By default, hub method parameters are inspected and resolved from DI if possible.
	This means you can directly use dependency injection in the methods themselves.
	Or you can use DI the old-fashioned way, with a constructor.

	- You can change the name of the action/method using the [HubMethodName] attribute.

	- You can override the OnConnectAsync() and OnDisconnectAsync() methods.
*/

[Authorize]
public class BaseHub(IServiceScopeFactory serviceScopeFactory) : Hub
{
	public async override Task OnConnectedAsync()
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;
		IDistributedCache cache = serviceProvider.GetRequiredService<IDistributedCache>();
		AppDbContext dbContext = serviceProvider.GetRequiredService<AppDbContext>();
		ISessionTrustChecker trustChecker = serviceProvider.GetRequiredService<ISessionTrustChecker>();
		ISessionConnectionRegistry connectionRegistry = serviceProvider.GetRequiredService<ISessionConnectionRegistry>();

		ClaimsPrincipal? user = Context.User;

		if (user == null)
			return;

		// The default authorization policy already requires an app-purpose, currently-trusted token
		// to reach this point (Services.Sessions.Authorization.AppSessionRequirement), so a SessionId
		// claim is always present here. Joining this group is what lets
		// Services.Sessions.Service.SessionRevocationNotifier reach exactly this device's own
		// connections - never the whole account/user - on revocation. Both this and the registration
		// below happen BEFORE the authoritative recheck further down, so a revoke committing in the gap
		// between SessionGuardHubFilter's own entry check and here still finds this connection
		// joinable/abortable rather than a blind spot until the next maintenance sweep.
		string? sessionId = user.FindFirstValue(SessionClaimTypes.SessionId);
		bool registered = false;
		if (sessionId is not null)
		{
			await Groups.AddToGroupAsync(Context.ConnectionId, Services.Sessions.Service.SessionRevocationNotifier.GroupName(sessionId));

			// Registers this exact connection so a later revocation can actually abort it even while
			// idle (ISessionConnectionRegistry), not merely notify it - see that interface's own doc
			// comment for the same-replica/cross-replica split.
			string? accountId = user.FindFirstValue(SessionClaimTypes.AccountContextId);
			string? deviceRegistrationId = user.FindFirstValue(SessionClaimTypes.DeviceRegistrationId);
			string? trustVersionClaim = user.FindFirstValue(SessionClaimTypes.TrustVersion);
			string? expClaim = user.FindFirstValue(JwtRegisteredClaimNames.Exp);
			if (accountId is not null && deviceRegistrationId is not null && long.TryParse(trustVersionClaim, out long trustVersion) && long.TryParse(expClaim, out long expSeconds))
			{
				// Captured as a local, not `this.Context`: a Hub instance is transient (see this file's
				// own top-of-file warning) - closing over `Context` (the HubCallerContext, owned by the
				// long-lived connection, not by any one transient Hub instance) rather than an implicit
				// `this` keeps this delegate from pinning this particular Hub instance alive.
				HubCallerContext callerContext = Context;
				// ExpiresAtUtc lets SessionMaintenanceHostedService abort an idle connection whose token has
				// since naturally expired without ever being revoked - a DB-independent, always-available
				// check (see that class's own doc comment).
				DateTimeOffset expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
				connectionRegistry.Register(new LiveSessionConnection(callerContext.ConnectionId, accountId, sessionId, deviceRegistrationId, trustVersion, expiresAtUtc, DateTimeOffset.UtcNow, () => callerContext.Abort()));
				registered = true;
			}
		}

		int receiverId = user.Id();
		DateTime now = DateTime.UtcNow;

		// Fresh start (plan/06): chats belonging to a superseded account context are not this context's
		// history, so their undelivered messages are neither delivered nor acknowledged here. Empty for
		// every account that never started over.
		IAccountContextBoundary contextBoundary = serviceProvider.GetRequiredService<IAccountContextBoundary>();
		// Materialized as an array: EF translates Contains over a local array/list, not over the
		// IReadOnlySet interface method.
		int[] excludedChatIds = [.. await contextBoundary.ExcludedLegacyChatIdsAsync(user, Context.ConnectionAborted)];

		MessageEntity[] candidateMessages =
			await dbContext.Messages
			.Where(m =>
				m.Chat.Users.Select(u => u.Id).Contains(receiverId)
				&& m.SenderId != receiverId
				&& !excludedChatIds.Contains(m.ChatId)
				&& !m.MessageStatusEntities.Any(ms => ms.ReceiverId == receiverId
					&& ms.Status == MessageStatus.Received))
			.ToArrayAsync();

		// Stage 07, deferred delivery: the legacy row keeps every past participant of a chat (that is what
		// keeps a removed member's archive readable), so membership - not the legacy user list - decides
		// what this reconnect may actually deliver and acknowledge. One shared check, the same one HTTP and
		// live delivery use: a member added later never reaches back before its own window, and a removed
		// member never reaches past its removal.
		IChatAccessAuthority accessAuthority = serviceProvider.GetRequiredService<IChatAccessAuthority>();
		IReadOnlyDictionary<int, ChatAccess> chatAccess = await accessAuthority.GetManyAsync(
			user, [.. candidateMessages.Select(m => m.ChatId).Distinct()], Context.ConnectionAborted);

		MessageEntity[] undeliveredMessages = [.. candidateMessages
			.Where(m => chatAccess.TryGetValue(m.ChatId, out ChatAccess? access) && access.PermitsMessageAt(m.CreatedAt))];

		MessageStatusEntity[] messageStatusEntities = undeliveredMessages.Select(m => new MessageStatusEntity(m.Id, receiverId, MessageStatus.Received, now)).ToArray();

		// Authoritative recheck, locked, immediately before the actual persistence - see
		// ISessionTrustChecker.CheckAndLockDeviceForMutationAsync's own doc comment. SessionGuardHubFilter's
		// own check ran once, BEFORE this method even started, and several awaited round trips (group join,
		// registration, the query above) sit between it and this point - this closes that gap for the
		// mutation/broadcast below specifically, same treatment SendMessage/ChatEntered already have. The
		// commit happens strictly BEFORE the broadcast loop - a connection admitted a moment before a
		// concurrent revoke's commit must never mark-and-publish AFTER that commit.
		TrustCheckResult trust = await TrustedMutation.RunAsync(dbContext, trustChecker, user, () =>
		{
			dbContext.AddRange(messageStatusEntities);
			return dbContext.SaveChangesAsync();
		}, Context.ConnectionAborted);

		if (!trust.IsTrusted)
		{
			if (registered)
			{
				connectionRegistry.Unregister(Context.ConnectionId);
			}
			Context.Abort();
			return;
		}

		string cacheKey = string.Format(CacheKeys.ConnectionEstablished, receiverId);
		await cache.SetAsync(cacheKey, true, DateTime.UtcNow.AddHours(1));

		if (undeliveredMessages.Length > 0)
		{
			IEnumerable<IGrouping<int, MessageEntity>> messagesByChats = undeliveredMessages.GroupBy(undeliveredMessage => undeliveredMessage.ChatId);

			var messagesByChatsThenBySenders = messagesByChats.Select(messagesByChat => new
			{
				ChatId = messagesByChat.Key,
				SendersByChat = messagesByChat.GroupBy(message => message.SenderId)
									.Select(messagesBySender => new
									{
										SenderId = messagesBySender.Key,
										Messages = messagesBySender.ToArray()
									})
									.ToArray()
			})
			.ToArray();

			foreach (var messageByChatThenBySender in messagesByChatsThenBySenders)
			{
				foreach (var messagesBySender in messageByChatThenBySender.SendersByChat)
				{
					IEnumerable<MessageEntity> messages = messagesBySender.Messages;
					MessageStatusUpdateEvent messageStatusUpdateEvent = new(messageByChatThenBySender.ChatId, receiverId, MessageStatus.Received, now, messages.Select(m => m.Id).ToArray());

					await SendToLegacyUsersAsync(
						Clients, contextBoundary, [messagesBySender.SenderId], messageByChatThenBySender.ChatId,
						LiveEvents.MessageStatusUpdate, messageStatusUpdateEvent, Context.ConnectionAborted);
				}
			}
		}

		await base.OnConnectedAsync();
	}

	/// <summary>
	/// Per-user live delivery with the fresh-start boundary applied (plan/06): SignalR addresses users by
	/// legacy id, which cannot tell a phone's superseded context from its current one.
	/// </summary>
	private static async Task SendToLegacyUsersAsync<T>(
		IHubCallerClients clients, IAccountContextBoundary boundary, int[] legacyUserIds, int legacyChatId,
		string eventName, T payload, CancellationToken ct)
	{
		LegacyRecipients delivery = await boundary.ResolveLegacyRecipientsAsync(legacyUserIds, legacyChatId, ct);
		if (delivery.UserIds.Count > 0)
		{
			await clients.GetUsersByIds([.. delivery.UserIds]).SendAsync(eventName, payload, ct);
		}
		if (delivery.ConnectionIds.Count > 0)
		{
			await clients.Clients(delivery.ConnectionIds).SendAsync(eventName, payload, ct);
		}
	}

	public async override Task OnDisconnectedAsync(Exception? exception)
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;
		IDistributedCache cache = serviceProvider.GetRequiredService<IDistributedCache>();

		// Safe cleanup regardless of whether this connection ever reached the registration point in
		// OnConnectedAsync (Unregister is a no-op for an unknown ConnectionId) - every disconnect path
		// (graceful stop, transport drop, or this.Abort() being called by the registry/filter itself)
		// ends here, so a connection can never be left registered after it is actually gone.
		serviceProvider.GetRequiredService<ISessionConnectionRegistry>().Unregister(Context.ConnectionId);

		ClaimsPrincipal? user = Context.User;

		if (user == null)
			return;

		int userId = user.Id();
		string connectionEstablishedKey = string.Format(CacheKeys.ConnectionEstablished, userId);
		string userCurrentChatIdKey = string.Format(CacheKeys.UserCurrentChatId, userId);

		await cache.RemoveAsync(connectionEstablishedKey);

		byte[]? currentChatIdBytes = await cache.GetAsync(userCurrentChatIdKey);
		if (currentChatIdBytes != null)
		{
			int currentChatId = System.Text.Json.JsonSerializer.Deserialize<int>(currentChatIdBytes);
			string chatEnteredKey = string.Format(CacheKeys.ChatEntered, userId, currentChatId);
			await cache.RemoveAsync(chatEnteredKey);
			await cache.RemoveAsync(userCurrentChatIdKey);
		}

		await base.OnDisconnectedAsync(exception);
	}

	[HubMethodName("send-message")]
	public async Task SendMessage(SendMessage sendMessageEvent)
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;
		AppDbContext dbContext = serviceProvider.GetRequiredService<AppDbContext>();
		INotificationDispatch notificationDispatch = serviceProvider.GetRequiredService<INotificationDispatch>();
		IPresenceService presenceService = serviceProvider.GetRequiredService<IPresenceService>();
		ILogger<BaseHub> logger = serviceProvider.GetRequiredService<ILogger<BaseHub>>();
		ISessionTrustChecker trustChecker = serviceProvider.GetRequiredService<ISessionTrustChecker>();

		ClaimsPrincipal? user = Context.User;

		if (user == null)
			return;

		int senderId = user.Id();
		int chatId = sendMessageEvent.ChatId;
		MessageEntity? insertedMessage = null;

		// A new account context may never publish into the old context's chats (plan/06 fresh start).
		IAccountContextBoundary contextBoundary = serviceProvider.GetRequiredService<IAccountContextBoundary>();
		if (await contextBoundary.IsLegacyChatExcludedAsync(user, chatId, Context.ConnectionAborted))
		{
			return;
		}

		IChatAccessAuthority accessAuthority = serviceProvider.GetRequiredService<IChatAccessAuthority>();

		// SessionGuardHubFilter already re-checked trust on ENTRY to this invocation, but that alone
		// cannot stop a request admitted a moment before a concurrent revoke's commit from still
		// persisting/publishing after it - a plain second read immediately before this insert would
		// still race the exact same way. TrustedMutation locks the device(+session) row inside the SAME
		// transaction that performs the insert, which is what actually serializes this write against
		// DeviceTrustService.RevokeDeviceAsync/RefreshTokenService.RotateAsync for this caller (see
		// ISessionTrustChecker.CheckAndLockDeviceForMutationAsync's own doc comment for the full
		// lock-ordering argument).
		//
		// Stage 07 adds the second serialization point inside the very same transaction: the chat's own
		// counter row, which every membership mutation allocates its change sequence from. Checking the
		// publish right under that lock is what makes "Промените на правата се сериализират със записите в
		// чата" true - a concurrent removal/leave either commits first (and this send is then refused) or
		// waits until this message has committed.
		bool mayPublish = false;
		TrustCheckResult trust = await TrustedMutation.RunAsync(dbContext, trustChecker, user, async () =>
		{
			ChatAccess access = await accessAuthority.LockChatAndAuthorizeAsync(user, chatId, Context.ConnectionAborted);
			mayPublish = access.CanSend;
			if (!mayPublish)
			{
				// A frozen archive is read-only, and a non-member has nothing here at all.
				return;
			}

			insertedMessage = dbContext.Messages.Add(new MessageEntity(sendMessageEvent.Content, senderId, chatId, DateTime.UtcNow)).Entity;
			await dbContext.SaveChangesAsync();
		}, Context.ConnectionAborted);

		if (!trust.IsTrusted)
		{
			Context.Abort();
			return;
		}

		if (!mayPublish)
		{
			return;
		}

		MessageEntity messageEntity = insertedMessage!;

		int[] chatParticipants = (await dbContext.Chats.Where(c => c.Id == sendMessageEvent.ChatId).Select(c => c.Users.Select(u => u.Id)).FirstOrDefaultAsync() ?? []).Except([senderId]).ToArray();

		// Only currently active members receive new content; a removed/departed participant keeps its
		// archive but is not a recipient of anything created after it left (the legacy participant row it
		// still has is what preserves that archive, so it cannot be used as the recipient list).
		int[] receiversIds = [.. await accessAuthority.ActiveLegacyRecipientsAsync(chatId, chatParticipants, Context.ConnectionAborted)];

		NewMessageCreatedEvent newMessageCreatedEvent = new(messageEntity.Id, messageEntity.ChatId, sendMessageEvent.TempId, messageEntity.CreatedAt);
		NewMessageEvent newMessageEvent = new(messageEntity.Id, senderId, messageEntity.ChatId, sendMessageEvent.Content, messageEntity.CreatedAt);

		await Clients
			.GetUserById(senderId)
			.SendAsync(LiveEvents.NewMessageCreated, newMessageCreatedEvent);

		// Recipient-side context isolation: a phone that started over keeps the same legacy user id, so a
		// plain user-id fan-out would deliver this old-context chat to its fresh context's connection.
		await SendToLegacyUsersAsync(
			Clients, contextBoundary, receiversIds, chatId, LiveEvents.NewMessage, newMessageEvent, Context.ConnectionAborted);

		List<MessageStatusEntity> messageStatusEntities = [];

		foreach (int receiverId in receiversIds)
		{
			bool isUserInChat = await presenceService.IsUserInChat(receiverId, chatId);
			bool isUserOnline = await presenceService.IsUserOnline(receiverId);

			MessageStatusEntity messageStatusEntity = new(messageEntity.Id, receiverId, default, DateTime.UtcNow);
			MessageStatusUpdateEvent messageStatusUpdateEvent = new(sendMessageEvent.ChatId, receiverId, default, DateTime.UtcNow, [messageEntity.Id]);
			messageStatusEntities.Add(messageStatusEntity);

			if (isUserInChat)
			{
				messageStatusEntity.Status = MessageStatus.Seen;
				messageStatusUpdateEvent.Status = MessageStatus.Seen;
			}
			else if (isUserOnline)
			{
				messageStatusEntity.Status = MessageStatus.Received;
				messageStatusUpdateEvent.Status = MessageStatus.Received;
			}
			else
				continue;

			await SendToLegacyUsersAsync(
				Clients, contextBoundary, [senderId], chatId,
				LiveEvents.MessageStatusUpdate, messageStatusUpdateEvent, Context.ConnectionAborted);
		}

		try
		{
			// Same fresh-start boundary as the live fan-out above, applied to the push: the payload is the
			// message text and the old chat id, and a device token is only known by legacy user id.
			IReadOnlyList<int> notifiableIds = await contextBoundary.NotifiableLegacyUserIdsAsync(
				receiversIds, chatId, Context.ConnectionAborted);
			if (notifiableIds.Count > 0)
			{
				await notificationDispatch.NotificationForNewMessage([.. notifiableIds], senderId, sendMessageEvent.Content, chatId);
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to dispatch push notifications for chat {ChatId}", chatId);
		}

		dbContext.MessagesStatus.AddRange(messageStatusEntities);
		await dbContext.SaveChangesAsync();
	}

	[HubMethodName("chat-entered")]
	public async Task ChatEntered(ChatEnteredEvent chatEnteredEvent)
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;
		IDistributedCache cache = serviceProvider.GetRequiredService<IDistributedCache>();
		AppDbContext dbContext = serviceProvider.GetRequiredService<AppDbContext>();
		ISessionTrustChecker trustChecker = serviceProvider.GetRequiredService<ISessionTrustChecker>();

		int chatId = chatEnteredEvent.ChatId;
		ClaimsPrincipal? user = Context.User;
		DateTime now = DateTime.UtcNow;

		if (user == null)
			return;

		int receiverId = user.Id();

		// Same fresh-start boundary as the connect-time scan: an excluded chat is not this context's
		// history, so entering it must not mark anything seen or reveal its messages.
		IAccountContextBoundary contextBoundary = serviceProvider.GetRequiredService<IAccountContextBoundary>();
		if (await contextBoundary.IsLegacyChatExcludedAsync(user, chatId, Context.ConnectionAborted))
		{
			return;
		}

		// Stage 07: a read-only archive publishes nothing. Entering a chat whose membership has ended must
		// neither mark its messages Seen for everyone else nor register this caller as present in it (which
		// would make a later sender's own fast path mark its message Seen for a non-member) - the private
		// device-side progress of an archive is stage 08's concern, not a public mark here.
		IChatAccessAuthority accessAuthority = serviceProvider.GetRequiredService<IChatAccessAuthority>();
		ChatAccess access = await accessAuthority.GetAsync(user, chatId, Context.ConnectionAborted);
		if (!access.CanSend)
		{
			return;
		}

		string cacheKey = string.Format(CacheKeys.ChatEntered, receiverId, chatId);
		string userCurrentChatIdKey = string.Format(CacheKeys.UserCurrentChatId, receiverId);
		await cache.SetAsync(cacheKey, chatId, DateTime.UtcNow.AddDays(1));
		await cache.SetAsync(userCurrentChatIdKey, chatId, DateTime.UtcNow.AddDays(1));

		MessageEntity[] unSeenMessages =
			[.. (await dbContext.Messages
				.Where(
					m => m.ChatId == chatEnteredEvent.ChatId
					&& m.SenderId != receiverId
					&& !m.MessageStatusEntities.Any(ms => ms.ReceiverId == receiverId && ms.Status == MessageStatus.Seen))
				.ToArrayAsync())
				// Only messages this membership actually permits: a participant added later must not mark
				// the chat's earlier history - which it never received - as Seen for its authors.
				.Where(m => access.PermitsMessageAt(m.CreatedAt))];

		if (unSeenMessages.Length == 0)
		{
			return;
		}

		IEnumerable<IGrouping<int, MessageEntity>> messagesByChats = unSeenMessages.GroupBy(undeliveredMessage => undeliveredMessage.ChatId);

		var messagesByChatsThenBySenders = messagesByChats.Select(messagesByChat => new
		{
			ChatId = messagesByChat.Key,
			SendersByChat = messagesByChat.GroupBy(message => message.SenderId)
								.Select(messagesBySender => new
								{
									SenderId = messagesBySender.Key,
									Messages = messagesBySender.ToArray()
								})
								.ToArray()
		})
		.ToArray();

		MessageStatusEntity[] messageStatusEntities = unSeenMessages.Select(m => new MessageStatusEntity(m.Id, receiverId, MessageStatus.Seen, now)).ToArray();

		// Same TOCTOU concern/fix as SendMessage above (see ISessionTrustChecker.CheckAndLockDeviceForMutationAsync's
		// own doc comment): this marks messages Seen and then broadcasts that status to each original
		// sender below - an authenticated mutation that publishes to ANOTHER user, not merely this
		// caller's own bookkeeping, so SessionGuardHubFilter's guard-on-entry alone is not enough here
		// either. Persist is locked+committed first; the broadcast loop only runs after.
		TrustCheckResult trust = await TrustedMutation.RunAsync(dbContext, trustChecker, user, () =>
		{
			dbContext.AddRange(messageStatusEntities);
			return dbContext.SaveChangesAsync();
		}, Context.ConnectionAborted);

		if (!trust.IsTrusted)
		{
			Context.Abort();
			return;
		}

		foreach (var messageByChatThenBySender in messagesByChatsThenBySenders)
		{
			foreach (var messagesBySender in messageByChatThenBySender.SendersByChat)
			{
				IEnumerable<MessageEntity> messages = messagesBySender.Messages;
				MessageStatusUpdateEvent messageStatusUpdateEvent = new(messageByChatThenBySender.ChatId, receiverId, MessageStatus.Seen, now, messages.Select(m => m.Id).ToArray());

				await SendToLegacyUsersAsync(
					Clients, contextBoundary, [messagesBySender.SenderId], messageByChatThenBySender.ChatId,
					LiveEvents.MessageStatusUpdate, messageStatusUpdateEvent, Context.ConnectionAborted);
			}
		}
	}

	[HubMethodName("chat-exited")]
	public async Task ChatExited(ChatExitedEvent chatExitedEvent)
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;
		IDistributedCache cache = serviceProvider.GetRequiredService<IDistributedCache>();
		ClaimsPrincipal? user = Context.User;

		if (user == null)
			return;

		int userId = user.Id();
		string cacheKey = string.Format(CacheKeys.ChatEntered, userId, chatExitedEvent.ChatId);
		string userCurrentChatIdKey = string.Format(CacheKeys.UserCurrentChatId, userId);
		await cache.RemoveAsync(cacheKey);
		await cache.RemoveAsync(userCurrentChatIdKey);
	}

	[HubMethodName("message-status-update-received")]
	public async Task MessageStatusUpdateReceived(MessageStatusUpdateConfirmation messageStatusUpdateConfirmation)
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;
		AppDbContext dbContext = serviceProvider.GetRequiredService<AppDbContext>();

		ClaimsPrincipal? user = Context.User;

		if (user == null)
			return;

		int senderId = user.Id();
		DateTime now = DateTime.UtcNow;

		MessageStatusEntity[] messageStatusEntities =
			await dbContext.MessagesStatus.Where(ms => ms.Message.ChatId == messageStatusUpdateConfirmation.ChatId && !ms.StatusUpdateDeliveryConfirmed && ms.Status == messageStatusUpdateConfirmation.Status).ToArrayAsync();


		foreach (MessageStatusEntity messageStatusEntity in messageStatusEntities)
		{
			messageStatusEntity.StatusUpdateDeliveryConfirmed = true;
		}

		dbContext.UpdateRange(messageStatusEntities);
		await dbContext.SaveChangesAsync();
	}
}
