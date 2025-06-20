using Database.Entities;
using Services.NotificationDispatch.Models;
using Services.Repositories.Base.Interface;

namespace Services.Repositories.DeviceNotificationConfig.Interface
{
    public interface IDeviceNotificationConfigRepository : IBaseRepository
    {
        void AddDeviceData(DeviceNotificationConfigEntity notificationEntity);
        Task<bool> UpdateDeviceData(int userId, string deviceId, bool isNotificationEnabled);
        Task<bool> DoesTokenExistForUser(string deviceToken, int userId);
        Task<bool> IsUserDeviceNotificationEnabled(string deviceId, int userId);
        Task<List<DeviceData>> FetchEnabledUserDeviceDataById (int userId);
        Task<List<DeviceData>> FetchEnabledUsersDevicesDataByIds (int[] userId);
    }
}