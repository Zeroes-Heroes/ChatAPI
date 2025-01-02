using Database.Context;
using Database.Entities;
using Microsoft.EntityFrameworkCore;
using Services.PushNotification.Models;
using Services.Repositories.PushNotification.Interface;

namespace Services.Repositories.PushNotification.Repository
{
    public class PushNotificationRepository(AppDbContext dbContext) : IPushNotificationRepository
    {
        public async Task<PushNotificationEntity> AddDeviceData(PushNotificationEntity notificationEntity)
        {
            notificationEntity = dbContext.PushNotifications.Add(notificationEntity).Entity;

            await SaveChangesAsync();
            return notificationEntity;
        }

        public async Task<PushNotificationEntity?> UpdateDeviceData(int userId, string deviceId, bool isNotificationEnabled)
        {
            PushNotificationEntity? notificationData = await dbContext.PushNotifications.FirstOrDefaultAsync(n => n.UserDevice.DeviceId == deviceId && n.UserId == userId);
            if (notificationData == null || notificationData.IsNotificationEnabled == isNotificationEnabled)
            {
                return null;
            }

            notificationData.IsNotificationEnabled = isNotificationEnabled;

            await SaveChangesAsync();

            return notificationData;
        }

        public Task<bool> CheckDeviceTokenExists(PushNotificationDTO notificationDTO, int userId) =>
            dbContext.PushNotifications.AnyAsync(p => p.Token == notificationDTO.Token && p.UserId == userId);

        public Task<PushNotificationEntity?> GetPushNotificationIdByDeviceId(string deviceId, int userId)
        {
            return dbContext.PushNotifications.FirstOrDefaultAsync(r => r.UserDevice.DeviceId == deviceId && r.UserId == userId);
        }

        public Task SaveChangesAsync() =>
            dbContext.SaveChangesAsync();
    }
}