using Services.Utilities;

namespace Services.NotificationDispatch.Interface
{
    public interface IAppleService
    {
        Task<Result> SendAsyncPushNotification(string deviceToken, object payload);
    }
}