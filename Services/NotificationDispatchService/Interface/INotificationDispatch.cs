using Services.Utilities;

namespace Services.NotificationDispatch.Interface
{
    public interface INotificationDispatch
    {
        Task<Result> NotificationForNewMessage(int[] receiversIds, int senderUserId, string message, int chatId);
        Task<Result> NotificationForNewCreateChat(int[] chatParticipantIds, int chatCreatorId, int chatId);
        Task<Result> NotificationForNewFriendshipRequest(int receiverUserId, string name);
        Task<Result> NotificationForAcceptFriendship(int notificationRecipientId, int requestSenderId);
        Task<Result> NotificationForRejectedFriendship(int notificationRecipientId, int requestSenderId);
        Task<Result> NotificationForBlockedFriendship(int notificationRecipientId, int requestSenderId);
    }
}