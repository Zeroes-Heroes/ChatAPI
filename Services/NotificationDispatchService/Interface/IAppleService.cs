using Services.NotificationDispatch.Models;
using Services.Utilities;

namespace Services.NotificationDispatch.Interface
{
    public interface IAppleService
    {
        Task<Result> SendAsyncPushNotification(string deviceToken, AppleNotificationPayload payload);
    }
}