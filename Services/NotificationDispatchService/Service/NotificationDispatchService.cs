using Database.Enums;
using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Caching.Distributed;
using Services.NotificationDispatch.Interface;
using Services.NotificationDispatch.Models;
using Services.Repositories.DeviceNotificationConfig.Interface;
using Services.Repositories.User.Interface;
using Services.Utilities;
using Services.Utilities.Statics;
using Services.Constants;

namespace Services.NotificationDispatch.Service
{
    public class NotificationDispatchService(IDeviceNotificationConfigRepository deviceNotificationConfigRepo, IUserRepository userRepo, IAppleService appleService, IDistributedCache cache) : INotificationDispatch
    {
        private async Task<Result> SendPushNotificationToApple(string deviceToken, NotificationBody notificationInfo)
        {
            AppleNotificationPayload payload = new()
            {
                aps = new ApplePushNotification
                {
                    Alert = new Alert
                    {
                        Title = notificationInfo.Title,
                        Body = notificationInfo.Body,
                    },
                    Sound = "default",
                    Route = notificationInfo.Route,
                    // "chatId" is provided only when the notification is for a chat; 
                    // in other cases, only the name of the screen to which the user should be navigated
                    // upon opening the notification is provided.
                    ChatId = notificationInfo?.ChatId?.ToString() ?? string.Empty,
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
        private async Task SendNotification(List<DeviceDataResponse> deviceDataList, NotificationBody notificationBody)
        {
            foreach (var deviceData in deviceDataList)
            {
                string deviceToken = deviceData.Token;
                if (deviceData.OS == OperatingSystemType.ios)
                {
                    await SendPushNotificationToApple(deviceToken, notificationBody);
                    continue;
                }

                if (deviceData.OS == OperatingSystemType.android)
                {
                    await SendPushNotificationToAndroid(deviceToken, notificationBody);
                    continue;
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

        private async Task NotifyOnlineUsersAsync(int[] receiversIds, int chatOwenId, NotificationBody notificationBody)
        {
            List<DeviceUserDataResponse> results = await deviceNotificationConfigRepo.FetchEnabledUsersDevicesDataByIds(receiversIds);
            foreach (int userId in receiversIds)
            {
                if (userId != chatOwenId)
                {
                    bool isUserOnline = await IsUserOnline(userId);
                    if (!isUserOnline) return;

                    var userDevices = results.Where(d => d.UserId == userId)
                        .Select(d => new DeviceDataResponse
                        {
                            OS = d.OS,
                            Token = d.Token,
                            IsNotificationEnabled = d.IsNotificationEnabled,
                        }).ToList();

                    await SendNotification(userDevices, notificationBody);
                }
            }
        }

        private async Task NotifyOnlineUserAsync(int userId, NotificationBody notificationBody)
        {
            bool isUserOnline = await IsUserOnline(userId);
            if (!isUserOnline) return;

            List<DeviceDataResponse> result = await deviceNotificationConfigRepo.FetchEnabledUserDeviceDataById(userId);

            await SendNotification(result, notificationBody);
        }

        private async Task<string> GetUserNameById(int userId) =>
            await userRepo.GetUserNameById(userId) ?? "";


        public async Task<Result> NotificationForNewMessage(int[] receiversIds, int senderUserId, string message, int chatId)
        {
            string userName = await GetUserNameById(senderUserId);

            NotificationBody notificationBody = new()
            {
                Title = $"{userName} send you a message",
                Body = message,
                Route = ScreenNames.ChatScreen,
                ChatId = chatId,
            };

            await NotifyOnlineUsersAsync(receiversIds, senderUserId, notificationBody);

            return Result.Success();
        }

        public async Task<Result> NotificationForNewChat(int[] chatParticipantIds, int chatCreatorId, int chatId)
        {
            string userName = await GetUserNameById(chatCreatorId);

            NotificationBody notificationBody = new()
            {
                Title = "New Created chat",
                Body = $"{userName} created chat with you.",
                Route = ScreenNames.ChatScreen,
                ChatId = chatId,
            };

            await NotifyOnlineUsersAsync(chatParticipantIds, chatCreatorId, notificationBody);

            return Result.Success();
        }

        public async Task<Result> NotificationForNewFriendshipRequest(int userId, string name)
        {
            NotificationBody notificationBody = new()
            {
                Title = "New Request",
                Body = $"{name} you send new friendship request",
                Route = ScreenNames.RequestsScreen,
            };

            await NotifyOnlineUserAsync(userId, notificationBody);

            return Result.Success();
        }

        public async Task<Result> NotificationAcceptFriendship(int notificationRecipientId, int requestSenderId)
        {
            string userName = await GetUserNameById(requestSenderId);

            NotificationBody notificationBody = new()
            {
                Title = "Accepted request",
                Body = $"{userName} accepted your friend request",
                Route = ScreenNames.ContactsScreen,
            };
            await NotifyOnlineUserAsync(notificationRecipientId, notificationBody);

            return Result.Success();
        }

        public async Task<Result> NotificationForRejectedFriendship(int notificationRecipientId, int requestSenderId)
        {
            string userName = await GetUserNameById(requestSenderId);

            NotificationBody notificationBody = new()
            {
                Title = "Rejected request",
                Body = $"{userName} reject your friend request",
                Route = ScreenNames.RejectedRequestsScreen,
            };
            await NotifyOnlineUserAsync(notificationRecipientId, notificationBody);

            return Result.Success();
        }

        public async Task<Result> NotificationForBlockedFriendship(int notificationRecipientId, int requestSenderId)
        {
            string userName = await GetUserNameById(requestSenderId);

            NotificationBody notificationBody = new()
            {
                Title = "Blocker request",
                Body = $"{userName} block your friend request",
                Route = ScreenNames.BlockedContactsScreen,
            };
            await NotifyOnlineUserAsync(notificationRecipientId, notificationBody);

            return Result.Success();
        }
    }
}