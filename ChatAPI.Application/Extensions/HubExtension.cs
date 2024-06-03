namespace Microsoft.AspNetCore.SignalR
{
	public static class HubExtension
	{
		public static IClientProxy GetUserById(this IHubClients<IClientProxy> clients, int userId)
			=> clients.User(userId.ToString());
	}
}
