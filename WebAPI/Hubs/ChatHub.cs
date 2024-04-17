using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace WebAPI.Hubs
{
	[Authorize]
	public class ChatHub : Hub
	{
		public Task SendMessage(string userId, string message) =>
			Clients.User(userId).SendAsync("ReceiveMessage", message);
	}
}