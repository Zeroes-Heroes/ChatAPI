using Database.Enums;
using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Caching.Distributed;
using Services.NotificationDispatch.Interface;
using Services.NotificationDispatch.Models;
using Services.Repositories.DeviceNotificationConfig.Interface;
using Services.Repositories.User.Interface;
using Services.Utilities;
using Services.Utilities.Statics;

namespace Services.NotificationDispatch.Service
{
    public class NotificationDispatchService(IDeviceNotificationConfigRepository deviceNotificationConfigRepo, IUserRepository userRepo, IAppleService appleService, IDistributedCache cache) : INotificationDispatch
    {
        private async Task<Result> SendPushNotificationToApple(string deviceToken, NotificationBody notificationInfo)
        {
            var payload = new
            {
                aps = new
                {
                    alert = new
                    {
                        title = notificationInfo.Title,
                        body = notificationInfo.Body,
                    },
                    sound = "default",
                    route = notificationInfo.Route,
                    // "chatId" is provided only when the notification is for a chat; 
                    // in other cases, only the name of the screen to which the user should be navigated
                    // upon opening the notification is provided.
                    chatId = notificationInfo?.ChatId,
                }
            };

            await appleService.SendAsyncPushNotification(deviceToken, payload);

            return Result.Success();
        }

        private async Task<Result> SendPushNotificationToAndroid(string deviceToken, NotificationBody notificationInfo)
        {
            var message = new Message()
            {
                Notification = new Notification
                {
                    Title = notificationInfo.Title,
                    Body = notificationInfo.Body,
                },
                Token = deviceToken,
                Data = new Dictionary<string, string>()
                {
                    {"route", notificationInfo.Route},
                    // "chatId" is provided only when the notification is for a chat; 
                    // in other cases, only the name of the screen to which the user should be navigated
                    // upon opening the notification is provided.
                    {"chatId", notificationInfo?.ChatId.ToString()}
                }
            };

            await FirebaseMessaging.DefaultInstance.SendAsync(message);

            return Result.Success();
        }

        //  IMPORTANT: There is a possibility that, with the change in the ability to manage which notifications
        //  to receive and which not, the method may start receiving an array of data for each device to which it
        //  should send a notification as the first parameter.
        private async Task SendNotification(int userId, NotificationBody notificationBody)
        {
            List<DeviceDataResponse> result = await deviceNotificationConfigRepo.FetchEnabledUserDeviceDataById(userId);

            foreach (var deviceData in result)
            {
                string deviceToken = deviceData.Token;
                if (deviceData.OS == OperatingSystemType.ios)
                {
                    await SendPushNotificationToApple(deviceToken, notificationBody);
                }

                if (deviceData.OS == OperatingSystemType.android)
                {
                    await SendPushNotificationToAndroid(deviceToken, notificationBody);
                }
            }
        }

        // TODO (Moved)
        private async Task<bool> IsUserOnline(int userId)
        {
            string connectionEstablishedCacheKey = string.Format(CacheKeys.ConnectionEstablished, userId);
            bool isUserOnline = await cache.GetAsync(connectionEstablishedCacheKey) != null;
            return isUserOnline;
        }

        private async Task<string> GetUserNameById(int userId) =>
            await userRepo.GetUserNameById(userId) ?? "";


        // Public methods responsible for sending notifications to users

        public async Task<Result> NotificationForNewMessage(int[] receiversIds, int senderUserId, string message, int chatId)
        {
            string userName = await GetUserNameById(senderUserId);

            NotificationBody notificationBody = new()
            {
                Title = $"{userName} send you a message",
                Body = message,
                Route = "Chats",
                ChatId = chatId,
            };

            for (int i = 0; i < receiversIds.Count(); i++)
            {
                if (i != senderUserId)
                {
                    bool isUserOnline = await IsUserOnline(i);
                    if (!isUserOnline)
                        return Result.Success();

                    await SendNotification(i, notificationBody);
                }
            }

            return Result.Success();
        }

        public async Task<Result> NotificationForNewCreateChat(int[] chatParticipantIds, int chatCreatorId, int chatId)
        {
            string userName = await GetUserNameById(chatCreatorId);

            NotificationBody notificationBody = new()
            {
                Title = "New Created chat",
                Body = $"{userName} created chat with you",
                Route = "Chats",
                ChatId = chatId,
            };

            for (int i = 0; i < chatParticipantIds.Count(); i++)
            {
                if (i != chatCreatorId)
                {
                    bool isUserOnline = await IsUserOnline(i);
                    if (!isUserOnline)
                        return Result.Success();

                    await SendNotification(i, notificationBody);
                }
            }

            return Result.Success();
        }

        public async Task<Result> NotificationForNewFriendshipRequest(int userId, string name)
        {
            bool isUserOnline = await IsUserOnline(userId);
            if (!isUserOnline)
                return Result.Success();

            NotificationBody notificationBody = new()
            {
                Title = "New Request",
                Body = $"{name} you send new friendship request",
                Route = "Requests",
            };

            await SendNotification(userId, notificationBody);

            return Result.Success();
        }

        public async Task<Result> NotificationForAcceptFriendship(int notificationRecipientId, int requestSenderId)
        {
            bool isUserOnline = await IsUserOnline(notificationRecipientId);
            if (!isUserOnline)
                return Result.Success();

            string userName = await GetUserNameById(requestSenderId);

            NotificationBody notificationBody = new()
            {
                Title = "Accepted request",
                Body = $"{userName} accepted your friend request",
                Route = "Contacts",
            };

            await SendNotification(notificationRecipientId, notificationBody);

            return Result.Success();
        }

        public async Task<Result> NotificationForRejectedFriendship(int notificationRecipientId, int requestSenderId)
        {
            bool isUserOnline = await IsUserOnline(notificationRecipientId);
            if (!isUserOnline)
                return Result.Success();

            string userName = await GetUserNameById(requestSenderId);

            NotificationBody notificationBody = new()
            {
                Title = "Rejected request",
                Body = $"{userName} reject your friend request",
                Route = "RejectedRequests",
            };

            await SendNotification(notificationRecipientId, notificationBody);

            return Result.Success();
        }

        public async Task<Result> NotificationForBlockedFriendship(int notificationRecipientId, int requestSenderId)
        {
            bool isUserOnline = await IsUserOnline(notificationRecipientId);
            if (!isUserOnline)
                return Result.Success();

            string userName = await GetUserNameById(requestSenderId);

            NotificationBody notificationBody = new()
            {
                Title = "Blocker request",
                Body = $"{userName} block your friend request",
                Route = "BlockedContacts",
            };

            await SendNotification(notificationRecipientId, notificationBody);

            return Result.Success();
        }
    }
}