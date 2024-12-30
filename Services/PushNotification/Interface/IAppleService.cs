using Services.Utilities;

namespace Services.PushNotification.Interface
{
    public interface IAppleService
    {
        Task<Result> SendAsyncPushNotification(string deviceToken, object payload);
    }
}