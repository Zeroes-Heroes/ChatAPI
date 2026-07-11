namespace Services.Presence.Interface
{
    public interface IPresenceService
    {
        Task<bool> IsUserOnline(int userId);
        Task<bool> IsUserInChat(int userId, int chatId);
        Task<int[]> FilterOfflineUsers(int[] userIds, int skipUserId);
    }
}
