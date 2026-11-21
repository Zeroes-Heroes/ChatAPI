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

		MessageEntity[] undeliveredMessages =
			await dbContext.Messages
			.Where(m =>
				m.Chat.Users.Select(u => u.Id).Contains(receiverId)
				&& m.SenderId != receiverId
				&& !m.MessageStatusEntities.Any(ms => ms.ReceiverId == receiverId
					&& ms.Status == MessageStatus.Received))
			.ToArrayAsync();

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

					await Clients
						.GetUserById(messagesBySender.SenderId)
						.SendAsync(LiveEvents.MessageStatusUpdate, messageStatusUpdateEvent);
				}
			}
		}

		await base.OnConnectedAsync();
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

		// SessionGuardHubFilter already re-checked trust on ENTRY to this invocation, but that alone
		// cannot stop a request admitted a moment before a concurrent revoke's commit from still
		// persisting/publishing after it - a plain second read immediately before this insert would
		// still race the exact same way. TrustedMutation locks the device(+session) row inside the SAME
		// transaction that performs the insert, which is what actually serializes this write against
		// DeviceTrustService.RevokeDeviceAsync/RefreshTokenService.RotateAsync for this caller (see
		// ISessionTrustChecker.CheckAndLockDeviceForMutationAsync's own doc comment for the full
		// lock-ordering argument).
		TrustCheckResult trust = await TrustedMutation.RunAsync(dbContext, trustChecker, user, () =>
		{
			insertedMessage = dbContext.Messages.Add(new MessageEntity(sendMessageEvent.Content, senderId, chatId, DateTime.UtcNow)).Entity;
			return dbContext.SaveChangesAsync();
		}, Context.ConnectionAborted);

		if (!trust.IsTrusted)
		{
			Context.Abort();
			return;
		}

		MessageEntity messageEntity = insertedMessage!;

		int[] receiversIds = (await dbContext.Chats.Where(c => c.Id == sendMessageEvent.ChatId).Select(c => c.Users.Select(u => u.Id)).FirstOrDefaultAsync() ?? []).Except([senderId]).ToArray();

		NewMessageCreatedEvent newMessageCreatedEvent = new(messageEntity.Id, messageEntity.ChatId, sendMessageEvent.TempId, messageEntity.CreatedAt);
		NewMessageEvent newMessageEvent = new(messageEntity.Id, senderId, messageEntity.ChatId, sendMessageEvent.Content, messageEntity.CreatedAt);

		await Clients
			.GetUserById(senderId)
			.SendAsync(LiveEvents.NewMessageCreated, newMessageCreatedEvent);

		await Clients
			.GetUsersByIds(receiversIds)
			.SendAsync(LiveEvents.NewMessage, newMessageEvent);

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

			await Clients
				.GetUserById(senderId)
				.SendAsync(LiveEvents.MessageStatusUpdate, messageStatusUpdateEvent);
		}

		try
		{
			await notificationDispatch.NotificationForNewMessage(receiversIds, senderId, sendMessageEvent.Content, chatId);
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
		string cacheKey = string.Format(CacheKeys.ChatEntered, receiverId, chatId);
		string userCurrentChatIdKey = string.Format(CacheKeys.UserCurrentChatId, receiverId);
		await cache.SetAsync(cacheKey, chatId, DateTime.UtcNow.AddDays(1));
		await cache.SetAsync(userCurrentChatIdKey, chatId, DateTime.UtcNow.AddDays(1));

		MessageEntity[] unSeenMessages =
			await dbContext.Messages
			.Where(
				m => m.ChatId == chatEnteredEvent.ChatId
				&& m.SenderId != receiverId
				&& !m.MessageStatusEntities.Any(ms => ms.ReceiverId == receiverId && ms.Status == MessageStatus.Seen))
			.ToArrayAsync();

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

				await Clients
					.GetUserById(messagesBySender.SenderId)
					.SendAsync(LiveEvents.MessageStatusUpdate, messageStatusUpdateEvent);
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
