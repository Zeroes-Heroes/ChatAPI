using Database.Context;
using Database.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Services.Extensions;
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
	[HubMethodName("send-message")]
	public async Task SendMessage(SendMessageEvent sendMessageEvent)
	{
		IServiceScope scope = serviceScopeFactory.CreateAsyncScope();

		AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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
	}
}