using Database.Entities;
using Services.PushNotification.Models;
using Services.Utilities;

namespace Services.PushNotification.Interface
{
    public interface IPushNotification
    {
        Task<Result<PushNotificationResponse>> SubscribeForPushNotification(PushNotificationDTO subscribeForPushNotification, int userId);
        Task<Result> UnsubscribeForPushNotification(int notificationId);
        Task<Result<PushNotificationResponseDeviceData>> GetDeviceDataForPushNotification(string deviceData, int userId);
    }
}