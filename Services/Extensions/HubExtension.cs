namespace Microsoft.AspNetCore.SignalR;

public static class HubExtension
{
	public static IClientProxy GetUserById(this IHubClients<IClientProxy> clients, int userId)
		=> clients.User(userId.ToString());

	public static IClientProxy GetUsersByIds(this IHubClients<IClientProxy> clients, int[] userIds) =>
		clients.Users(userIds.Select(ui => ui.ToString()));
}
