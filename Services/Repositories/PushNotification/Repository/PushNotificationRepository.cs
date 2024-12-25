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
            notificationEntity = dbContext.PushNotification.Add(notificationEntity).Entity;

            await SaveChangesAsync();
            return notificationEntity;
        }

        public async Task<PushNotificationEntity?> UpdateDeviceData(int notificationId)
        {
            PushNotificationEntity? notificationData = await dbContext.PushNotification.FirstOrDefaultAsync(n => n.Id == notificationId);
            if (notificationData == null)
            {
                return null;
            }

            notificationData.IsTurnOnNotification = !notificationData.IsTurnOnNotification;

            await SaveChangesAsync();

            return notificationData;
        }

        public Task<bool> DoesDeviceTokenExist(PushNotificationDTO notificationDTO, int userId) =>
            dbContext.PushNotification.AnyAsync(p => p.Token == notificationDTO.Token && p.UserId == userId);

        public Task<PushNotificationEntity?> GetPushNotificationIdByDeviceId(int deviceId, int userId) =>
            dbContext.PushNotification.FirstOrDefaultAsync(r => r.DeviceId == deviceId && r.UserId == userId);

        public Task SaveChangesAsync() =>
            dbContext.SaveChangesAsync();
    }
}