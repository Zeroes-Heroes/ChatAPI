using Database.Context;
using Database.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Services.Extensions;
using Services.Hubs.Enums;
using Services.Hubs.Models;
using Services.Utilities.Statics;

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
	This means you can directly use dependancy injection in the methods themselves.
	Or you can use DI the old-fashioned way, with a constructor.

	- You can change the name of the action/method using the [HubMethodName] attribute.

	- You can ovverride the OnConnectAsync() and OnDisconnectAsync() methods.
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

		string cacheKey = string.Format(CacheKeys.ConnectionEstablished, Context.User.Id());
		await cache.SetAsync(cacheKey, true, DateTime.UtcNow.AddDays(1));

		int receiverId = Context.User.Id();
		DateTime now = DateTime.UtcNow;

		MessageEntity[] undeliveredMessages =
			await dbContext.Messages.Where(m => m.Chat.Users.Select(u => u.Id).Contains(receiverId) && !m.MessageStatusEntities.Any(ms => ms.ReceiverId == receiverId && ms.Status == 1)).ToArrayAsync();
		MessageStatusEntity[] messageStatusEntities = undeliveredMessages.Select(m => new MessageStatusEntity(m.Id, receiverId, 1, now)).ToArray();

		foreach (MessageEntity undeliveredMessage in undeliveredMessages)
		{
			MessageStatusUpdateEvent messageStatusUpdateEvent = new(undeliveredMessage.ChatId, receiverId, MessageStatus.Received, now);

			await Clients
				.GetUserById(undeliveredMessage.SenderId)
				.SendAsync(LiveEvents.MessageStatusUpdate, messageStatusUpdateEvent);

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

		string connectionEstablishedKey = string.Format(CacheKeys.ConnectionEstablished, Context.User.Id());
		string chatEnteredKey = string.Format(CacheKeys.ChatEntered, Context.User.Id());
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

		int senderId = Context.User.Id();
		MessageEntity messageEntity = new(sendMessageEvent.Content, senderId, sendMessageEvent.ChatId, DateTime.UtcNow);
		messageEntity = dbContext.Messages.Add(messageEntity).Entity;
		await dbContext.SaveChangesAsync();

		int[] userIds = (await dbContext.Chats.Where(c => c.Id == sendMessageEvent.ChatId).Select(c => c.Users.Select(u => u.Id)).FirstOrDefaultAsync()).Except([senderId]).ToArray();

		NewMessageCreatedEvent newMessageCreatedEvent = new(messageEntity.Id, messageEntity.ChatId, sendMessageEvent.TempId, messageEntity.CreatedAt);
		NewMessageEvent newMessageEvent = new(messageEntity.Id, senderId, messageEntity.ChatId, sendMessageEvent.Content, messageEntity.CreatedAt);

		await Clients
			.GetUserById(senderId)
			.SendAsync(LiveEvents.NewMessageCreated, newMessageCreatedEvent);

		await Clients
			.GetUsersByIds(userIds)
			.SendAsync(LiveEvents.NewMessage, newMessageEvent);

		List<MessageStatusEntity> messageStatusEntities = [];

		foreach (int userId in userIds)
		{
			string chatEnteredCacheKey = string.Format(CacheKeys.ChatEntered, userId);
			string connectionEstablishedCacheKey = string.Format(CacheKeys.ConnectionEstablished, userId);
			byte[]? bytes = (await cache.GetAsync(chatEnteredCacheKey));
			bool isUserInChat = bytes != null;
			bool isUserOnline = (await cache.GetAsync(connectionEstablishedCacheKey)) != null;

			MessageStatusEntity messageStatusEntity = new(messageEntity.Id, userId, 2, DateTime.UtcNow);
			MessageStatusUpdateEvent messageStatusUpdateEvent = new(sendMessageEvent.ChatId, userId, MessageStatus.Sent, DateTime.UtcNow);

			if (isUserInChat)
			{
				messageStatusEntity.Status = 2;
				messageStatusUpdateEvent.Status = MessageStatus.Seen;
			}
			else if (isUserOnline)
			{
				messageStatusEntity.Status = 1;
				messageStatusUpdateEvent.Status = MessageStatus.Received;
			}
			else
			{
				messageStatusEntity.Status = (int)MessageStatus.Sent;
			}

			messageStatusEntities.Add(messageStatusEntity);

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

		string cacheKey = string.Format(CacheKeys.ChatEntered, Context.User.Id());
		await cache.SetAsync(cacheKey, chatId, DateTime.UtcNow.AddDays(1));

		int receiverId = Context.User.Id();
		DateTime now = DateTime.UtcNow;

		MessageEntity[] unSeenMessages =
				await dbContext.Messages.Where(m => m.ChatId == chatEnteredEvent.ChatId && !m.MessageStatusEntities.Any(ms => ms.ReceiverId == receiverId && ms.Status == 2)).ToArrayAsync();
		MessageStatusEntity[] messageStatusEntities = unSeenMessages.Select(m => new MessageStatusEntity(m.Id, receiverId, 2, now)).ToArray();

		foreach (MessageEntity undeliveredMessage in unSeenMessages)
		{
			MessageStatusUpdateEvent messageStatusUpdateEvent = new(undeliveredMessage.ChatId, receiverId, MessageStatus.Seen, now);

			await Clients
				.GetUserById(undeliveredMessage.SenderId)
				.SendAsync(LiveEvents.MessageStatusUpdate, messageStatusUpdateEvent);

		}

		dbContext.AddRange(messageStatusEntities);
		await dbContext.SaveChangesAsync();
	}

	[HubMethodName("chat-exited")]
	public async Task ChatExited()
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;
		IDistributedCache cache = serviceProvider.GetRequiredService<IDistributedCache>();

		string cacheKey = string.Format(CacheKeys.ChatEntered, Context.User.Id());
		await cache.RemoveAsync(cacheKey);
	}

	[HubMethodName("message-status-update-received")]
	public async Task MessageStatusUpdateReceived(MessageStatusUpdateConfirmation messageStatusUpdateConfirmation)
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();
		IServiceProvider serviceProvider = scope.ServiceProvider;
		AppDbContext dbContext = serviceProvider.GetRequiredService<AppDbContext>();

		int senderId = Context.User.Id();
		DateTime now = DateTime.UtcNow;

		MessageStatusEntity[] messageStatusEntities =
			await dbContext.MessagesStatus.Where(ms => ms.Message.ChatId == messageStatusUpdateConfirmation.ChatId && !ms.StatusUpdateDeliveryConfirmed && ms.Status == (int)messageStatusUpdateConfirmation.Status).ToArrayAsync();


		foreach (MessageStatusEntity messageStatusEntity in messageStatusEntities)
		{
			messageStatusEntity.StatusUpdateDeliveryConfirmed = true;
		}

		dbContext.UpdateRange(messageStatusEntities);
		await dbContext.SaveChangesAsync();
	}
}