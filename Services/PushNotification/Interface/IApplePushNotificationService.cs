using Services.Utilities;

namespace Services.PushNotification.Interface
{
    public interface IApplePushNotificationService
    {
        Task<Result> SendAsyncPushNotification(string deviceToken, object payload);
    }
}