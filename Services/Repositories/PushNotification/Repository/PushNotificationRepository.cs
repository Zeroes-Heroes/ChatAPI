using Database.Context;
using Database.Entities;
using Microsoft.EntityFrameworkCore;
using Services.Repositories.PushNotification.Interface;

namespace Services.Repositories.PushNotification.Repository
{
    public class PushNotificationRepository(AppDbContext dbContext) : IPushNotificationRepository
    {
        public void AddDeviceData(PushNotificationEntity notificationEntity)
        {
            dbContext.PushNotifications.Add(notificationEntity);
        }

        public async Task<bool> UpdateDeviceData(int userId, string deviceId, bool isNotificationEnabled)
        {
            PushNotificationEntity? notificationData = await dbContext.PushNotifications.FirstOrDefaultAsync(n => n.UserDevice.DeviceId == deviceId && n.UserId == userId);
            if (notificationData == null || notificationData.IsNotificationEnabled == isNotificationEnabled)
            {
                return false;
            }

            notificationData.IsNotificationEnabled = isNotificationEnabled;

            await SaveChangesAsync();

            return true;
        }

        public Task<bool> DoesDeviceTokenExist(string deviceToken, int userId) =>
            dbContext.PushNotifications.AnyAsync(p => p.Token == deviceToken && p.UserId == userId);

        public Task<bool> IsUserDeviceNotificationEnabled(string deviceId, int userId) =>
            dbContext.PushNotifications
                .Where(r => r.UserDevice.DeviceId == deviceId && r.UserId == userId)
                .Select(p => p.IsNotificationEnabled)
                .SingleOrDefaultAsync();

        public Task SaveChangesAsync() =>
            dbContext.SaveChangesAsync();
    }
}