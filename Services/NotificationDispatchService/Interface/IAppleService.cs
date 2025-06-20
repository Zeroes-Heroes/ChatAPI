using Services.NotificationDispatch.Models;
using Services.Utilities;

namespace Services.NotificationDispatch.Interface
{
    public interface IAppleService
    {
        Task SendPushNotification(string deviceToken, AppleNotificationPayload payload);
    }
}