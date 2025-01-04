using Database.Context;
using Database.Entities;
using Microsoft.EntityFrameworkCore;
using Services.Repositories.PushNotification.Interface;

namespace Services.Repositories.PushNotification.Repository
{
    public class PushNotificationRepository(AppDbContext dbContext) : IPushNotificationRepository
    {
        public async Task AddDeviceData(PushNotificationEntity notificationEntity)
        {
            notificationEntity = dbContext.PushNotifications.Add(notificationEntity).Entity;

            await SaveChangesAsync();
            return;
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

        public async Task<bool?> IsUserDeviceNotificationEnabled(string deviceId, int userId)
        {
            var result = await dbContext.PushNotifications.FirstOrDefaultAsync(r => r.UserDevice.DeviceId == deviceId && r.UserId == userId);
            if (result == null)
            {
                return null;
            }
            return result.IsNotificationEnabled;
        }

        public Task SaveChangesAsync() =>
            dbContext.SaveChangesAsync();
    }
}