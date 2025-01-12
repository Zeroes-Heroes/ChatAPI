using Database.Context;
using Database.Entities;
using Database.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Services.Extensions;
using Services.Hubs.Models;
using Services.NotificationDispatch.Interface;
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

		ClaimsPrincipal? user = Context.User;

		if (user == null)
			return;

		int receiverId = user.Id();
		string cacheKey = string.Format(CacheKeys.ConnectionEstablished, receiverId);
		await cache.SetAsync(cacheKey, true, DateTime.UtcNow.AddDays(1));

		DateTime now = DateTime.UtcNow;

		MessageEntity[] undeliveredMessages =
			await dbContext.Messages
			.Where(m =>
				m.Chat.Users.Select(u => u.Id).Contains(receiverId)
				&& m.SenderId != receiverId
				&& !m.MessageStatusEntities.Any(ms => ms.ReceiverId == receiverId
					&& ms.Status == MessageStatus.Received))
			.ToArrayAsync();

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

		MessageStatusEntity[] messageStatusEntities = undeliveredMessages.Select(m => new MessageStatusEntity(m.Id, receiverId, MessageStatus.Received, now)).ToArray();

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

		dbContext.AddRange(messageStatusEntities);
		await dbContext.SaveChangesAsync();

		await base.OnConnectedAsync();
	}

	public async override Task OnDisconnectedAsync(Exception? exception)
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;
		IDistributedCache cache = serviceProvider.GetRequiredService<IDistributedCache>();

		ClaimsPrincipal? user = Context.User;

		if (user == null)
			return;

		int userId = user.Id();
		string connectionEstablishedKey = string.Format(CacheKeys.ConnectionEstablished, userId);
		string chatEnteredKey = string.Format(CacheKeys.ChatEntered, userId);
		await cache.RemoveAsync(connectionEstablishedKey);
		await cache.RemoveAsync(chatEnteredKey);

		await base.OnDisconnectedAsync(exception);
	}

	[HubMethodName("send-message")]
	public async Task SendMessage(SendMessage sendMessageEvent)
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;
		IDistributedCache cache = serviceProvider.GetRequiredService<IDistributedCache>();
		AppDbContext dbContext = serviceProvider.GetRequiredService<AppDbContext>();
		INotificationDispatch notificationDispatch = serviceProvider.GetRequiredService<INotificationDispatch>();

		ClaimsPrincipal? user = Context.User;

		if (user == null)
			return;

		int senderId = user.Id();
		int chatId = sendMessageEvent.ChatId;
		MessageEntity messageEntity = new(sendMessageEvent.Content, senderId, chatId, DateTime.UtcNow);
		messageEntity = dbContext.Messages.Add(messageEntity).Entity;
		await dbContext.SaveChangesAsync();

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
			string chatEnteredCacheKey = string.Format(CacheKeys.ChatEntered, receiverId, chatId);
			string connectionEstablishedCacheKey = string.Format(CacheKeys.ConnectionEstablished, receiverId);
			byte[]? bytes = (await cache.GetAsync(chatEnteredCacheKey));
			bool isUserInChat = bytes != null;
			bool isUserOnline = (await cache.GetAsync(connectionEstablishedCacheKey)) != null;

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

			// Send a notification to each user participating in the chat, excluding the user who sender the message
			notificationDispatch.NotificationForNewMessage(receiversIds, senderId, sendMessageEvent.Content, chatId);

			await Clients
				.GetUserById(senderId)
				.SendAsync(LiveEvents.MessageStatusUpdate, messageStatusUpdateEvent);
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

		int chatId = chatEnteredEvent.ChatId;
		ClaimsPrincipal? user = Context.User;
		DateTime now = DateTime.UtcNow;

		if (user == null)
			return;

		int receiverId = user.Id();
		string cacheKey = string.Format(CacheKeys.ChatEntered, receiverId, chatId);
		await cache.SetAsync(cacheKey, chatId, DateTime.UtcNow.AddDays(1));

		MessageEntity[] unSeenMessages =
			await dbContext.Messages
			.Where(
				m => m.ChatId == chatEnteredEvent.ChatId
				&& m.SenderId != receiverId
				&& !m.MessageStatusEntities.Any(ms => ms.ReceiverId == receiverId && ms.Status == MessageStatus.Seen))
			.ToArrayAsync();

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

		dbContext.AddRange(messageStatusEntities);
		await dbContext.SaveChangesAsync();
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

		string cacheKey = string.Format(CacheKeys.ChatEntered, user.Id(), chatExitedEvent.ChatId);
		await cache.RemoveAsync(cacheKey);
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