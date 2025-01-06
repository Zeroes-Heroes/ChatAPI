using Database.Entities;
using Services.PushNotification.Models;
using Services.Repositories.Base.Interface;

namespace Services.Repositories.PushNotification.Interface
{
    public interface IPushNotificationRepository : IBaseRepository
    {
        void AddDeviceData(PushNotificationEntity notificationEntity);
        Task<bool> UpdateDeviceData(int userId, string deviceId, bool isNotificationEnabled);
        Task<bool> DoesDeviceTokenExist(string deviceToken, int userId);
        Task<bool> IsUserDeviceNotificationEnabled(string deviceId, int userId);
    }
}