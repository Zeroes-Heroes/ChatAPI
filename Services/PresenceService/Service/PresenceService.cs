using Microsoft.Extensions.Caching.Distributed;
using Services.Presence.Interface;
using Services.Utilities.Statics;

namespace Services.Presence.Service
{
    public class PresenceService(IDistributedCache cache) : IPresenceService
    {
        public async Task<bool> IsUserOnline(int userId)
        {
            string cacheKey = string.Format(CacheKeys.ConnectionEstablished, userId);
            return await cache.GetAsync(cacheKey) != null;
        }

        public async Task<bool> IsUserInChat(int userId, int chatId)
        {
            string cacheKey = string.Format(CacheKeys.ChatEntered, userId, chatId);
            return await cache.GetAsync(cacheKey) != null;
        }

        public async Task<int[]> FilterOfflineUsers(int[] userIds, int skipUserId)
        {
            int[] candidates = userIds.Where(id => id != skipUserId).ToArray();
            bool[] onlineStatuses = await Task.WhenAll(candidates.Select(IsUserOnline));
            return candidates.Where((id, index) => !onlineStatuses[index]).ToArray();
        }
    }
}
