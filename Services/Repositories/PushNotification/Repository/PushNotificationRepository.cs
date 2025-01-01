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

        public async Task<PushNotificationEntity?> UpdateDeviceData(int userId, string deviceId, bool isNotificationStatus)
        {
            PushNotificationEntity? notificationData = await dbContext.PushNotification.FirstOrDefaultAsync(n => n.UserDevice.DeviceId == deviceId && n.UserId == userId);
            if (notificationData == null || notificationData.IsTurnOnNotification == isNotificationStatus)
            {
                return null;
            }

            notificationData.IsTurnOnNotification = isNotificationStatus;

            await SaveChangesAsync();

            return notificationData;
        }

        public Task<bool> DoesDeviceTokenExist(PushNotificationDTO notificationDTO, int userId) =>
            dbContext.PushNotification.AnyAsync(p => p.Token == notificationDTO.Token && p.UserId == userId);

        public Task<PushNotificationEntity?> GetPushNotificationIdByDeviceId(string deviceId, int userId)
        {
            return dbContext.PushNotification.FirstOrDefaultAsync(r => r.UserDevice.DeviceId == deviceId && r.UserId == userId);
        }

        public async Task<List<DeviceDataResponse>> FetchEnabledUserDeviceDataById(int userId)
        {
            return await dbContext.PushNotification.Where(r => r.UserId == userId && r.IsTurnOnNotification == true)
                .Select(pushNotification => new DeviceDataResponse
                {
                    OS = pushNotification.OS,
                    Token = pushNotification.Token,
                    IsTurnOnNotification = pushNotification.IsTurnOnNotification,
                }).ToListAsync();
        }

        public Task SaveChangesAsync() =>
            dbContext.SaveChangesAsync();
    }
}