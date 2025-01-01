using Services.PushNotification.Models;
using Services.Utilities;

namespace Services.PushNotification.Interface
{
    public interface IPushNotification
    {
        Task<Result> SubscribeForPushNotification(PushNotificationDTO subscribeForPushNotification, int userId, string deviceId);
        Task<Result> ChangePushNotificationStatus(int userId, string deviceId, ChangeStatusRequest request);
        Task<Result<PushNotificationResponseDeviceData>> GetPushNotificationData(string deviceId, int userId);
        Task<Result> NotificationForNewMessage(int userId, string name, string message, int chatId);
        Task<Result> NotificationForNewCreateChat(int userId, string name, int chatId);
        Task<Result> NotificationForNewFriendshipRequest(int receiverUserId, string name);
        Task<Result> NotificationForAcceptFriendship(int notificationRecipientId, int requestSenderId);
        Task<Result> NotificationForRejectedFriendship(int notificationRecipientId, int requestSenderId);
        Task<Result> NotificationForBlockedFriendship(int notificationRecipientId, int requestSenderId);
    }
}