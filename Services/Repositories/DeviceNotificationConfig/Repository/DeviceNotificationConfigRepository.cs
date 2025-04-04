using Database.Context;
using Database.Entities;
using Microsoft.EntityFrameworkCore;
using Services.NotificationDispatch.Models;
using Services.Repositories.DeviceNotificationConfig.Interface;

namespace Services.Repositories.DeviceNotificationConfig.Repository
{
    public class DeviceNotificationConfigRepository(AppDbContext dbContext) : IDeviceNotificationConfigRepository
    {
        public void AddDeviceData(DeviceNotificationConfigEntity notificationEntity)
        {
            dbContext.DeviceNotificationsConfig.Add(notificationEntity);
        }

        public async Task<bool> UpdateDeviceData(int userId, string deviceId, bool isNotificationEnabled)
        {
            DeviceNotificationConfigEntity? notificationData = await dbContext.DeviceNotificationsConfig.FirstOrDefaultAsync(n => n.UserDevice.DeviceId == deviceId && n.UserId == userId);
            if (notificationData == null || notificationData.IsNotificationEnabled == isNotificationEnabled)
            {
                return false;
            }

            notificationData.IsNotificationEnabled = isNotificationEnabled;

            return true;
        }

        public Task<bool> DoesTokenExistForUser(string deviceToken, int userId) =>
            dbContext.DeviceNotificationsConfig.AnyAsync(p => p.Token == deviceToken && p.UserId == userId);

        public Task<bool> IsUserDeviceNotificationEnabled(string deviceId, int userId) =>
            dbContext.DeviceNotificationsConfig
                .Where(r => r.UserDevice.DeviceId == deviceId && r.UserId == userId)
                .Select(p => p.IsNotificationEnabled)
                .SingleOrDefaultAsync();


        public async Task<List<DeviceDataResponse>> FetchEnabledUserDeviceDataById(int userId)
        {
            return await dbContext.DeviceNotificationsConfig.Where(r => r.UserId == userId && r.IsNotificationEnabled == true)
                .Select(pushNotification => new DeviceDataResponse
                {
                    OS = pushNotification.OperatingSystem,
                    Token = pushNotification.Token,
                    IsNotificationEnabled = pushNotification.IsNotificationEnabled,
                }).ToListAsync();
        }

        public async Task<List<DeviceUserDataResponse>> FetchEnabledUsersDevicesDataByIds(int[] userIds)
        {
            return await dbContext.DeviceNotificationsConfig.Where(r => userIds.Contains(r.UserId) && r.IsNotificationEnabled == true)
                .Select(pushNotification => new DeviceUserDataResponse
                {
                    OS = pushNotification.OperatingSystem,
                    Token = pushNotification.Token,
                    IsNotificationEnabled = pushNotification.IsNotificationEnabled,
                    UserId = pushNotification.UserId
                }).ToListAsync();
        }

        public Task SaveChangesAsync() =>
            dbContext.SaveChangesAsync();
    }
}