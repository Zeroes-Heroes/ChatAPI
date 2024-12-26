using Database.Entities;
using Services.PushNotification.Models;
using Services.Utilities;

namespace Services.PushNotification.Interface
{
    public interface IPushNotification
    {
        Task<Result<PushNotificationResponse>> SubscribeForPushNotification(PushNotificationDTO subscribeForPushNotification, int userId);
        Task<Result> ChangePushNotificationStatus(int userId, string deviceId, ChangeStatusRequest request);
        Task<Result<PushNotificationResponseDeviceData>> GetPushNotificationData(string deviceId, int userId);
    }
}