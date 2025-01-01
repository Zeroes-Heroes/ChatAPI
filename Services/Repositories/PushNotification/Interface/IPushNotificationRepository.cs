using Database.Entities;
using Services.PushNotification.Models;
using Services.Repositories.Base.Interface;

namespace Services.Repositories.PushNotification.Interface
{
    public interface IPushNotificationRepository : IBaseRepository
    {
        Task<PushNotificationEntity> AddDeviceData(PushNotificationEntity notificationEntity);
        Task<PushNotificationEntity?> UpdateDeviceData(int userId, string deviceId, bool isNotificationStatus);
        Task<bool> DoesDeviceTokenExist(PushNotificationDTO notificationDTO, int userId);
        Task<PushNotificationEntity?> GetPushNotificationIdByDeviceId(string deviceId, int userId);
        Task<List<DeviceDataResponse>> FetchEnabledUserDeviceDataById (int userId);
    }
}