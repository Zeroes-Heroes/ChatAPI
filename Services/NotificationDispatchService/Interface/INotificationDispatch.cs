using Services.Utilities;

namespace Services.NotificationDispatch.Interface
{
    public interface INotificationDispatch
    {
        /// <summary>
        /// // Send a notification to each user participating in the chat, excluding the user who sender the message
        /// </summary>
        Task NotificationForNewMessage(int[] receiversIds, int senderUserId, string message, int chatId);
        /// <summary>
        /// Send a notification to each user participating in the chat, excluding the user who created the chat
        /// </summary>
        Task NotificationForNewChat(int[] chatParticipantIds, int chatCreatorId, int chatId);
        Task NotificationForNewFriendshipRequest(int receiverUserId, string name);
        Task NotificationAcceptFriendship(int notificationRecipientId, int requestSenderId);
        Task NotificationForRejectedFriendship(int notificationRecipientId, int requestSenderId);
        Task NotificationForBlockedFriendship(int notificationRecipientId, int requestSenderId);
    }
}