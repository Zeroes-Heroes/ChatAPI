using Database.Entities;
using Services.PushNotification.Models;
using Services.Repositories.Base.Interface;

namespace Services.Repositories.PushNotification.Interface
{
    public interface IPushNotificationRepository : IBaseRepository
    {
        Task<PushNotificationEntity> AddDeviceData(PushNotificationEntity notificationEntity);
        Task<PushNotificationEntity?> UpdateDeviceData(int notificationId);
        Task<bool> DoesDeviceTokenExist(PushNotificationDTO notificationDTO, int userId);
        Task<PushNotificationEntity?> GetPushNotificationIdByDeviceId(int deviceId, int userId);
    }
}