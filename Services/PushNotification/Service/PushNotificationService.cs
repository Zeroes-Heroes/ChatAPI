using Database.Entities;
using Database.Enums;
using FirebaseAdmin.Messaging;
using Services.PushNotification.Interface;
using Services.PushNotification.Models;
using Services.Repositories.OperationSystem.Interface;
using Services.Repositories.PushNotification.Interface;
using Services.Repositories.User.Interface;
using Services.Utilities;

namespace Services.PushNotification.Service
{
    public class PushNotificationService(IPushNotificationRepository pushNotificationRepo, IUserRepository userRepo, IAppleService appleService, IOperationSystemRepository operationSystemRepo) : IPushNotification
    {
        public async Task<Result> SubscribeForPushNotification(PushNotificationDTO deviceData, int userId, string deviceId)
        {
            bool doesDeviceTokenExist = await pushNotificationRepo.DoesDeviceTokenExist(deviceData, userId);
            if (doesDeviceTokenExist)
            {
                return Result.Failure("This Device token already exists");
            };

            var userDevice = await userRepo.GetIdByDeviceId(deviceId);
            if (userDevice == null)
            {
                return Result.Failure("Device Id doesn't exists");
            }

            var operationSystemId = await operationSystemRepo.GetOperationSystemId(deviceData.OS);
            if (operationSystemId == null)
            {
                return Result.Failure($"This operating system {deviceData.OS} is not maintained");
            }

            PushNotificationEntity notificationEntity = new(operationSystemId.Value, deviceData.Token, IsTurnOnNotification: true, UserId: userId, userDevice.Id);

            await pushNotificationRepo.AddDeviceData(notificationEntity);

            return Result.Success();
        }

        public async Task<Result> ChangePushNotificationStatus(int userId, string deviceId, ChangeStatusRequest request)
        {
            PushNotificationEntity? result = await pushNotificationRepo.UpdateDeviceData(userId, deviceId, request.IsNotificationStatus);
            if (result == null)
            {
                return Result.Failure("No data found or new record is the same with old record");
            }

            return Result.Success();
        }

        public async Task<Result<PushNotificationResponseDeviceData>> GetPushNotificationData(string deviceId, int userId)
        {
            PushNotificationEntity? resultPushNotification = await pushNotificationRepo.GetPushNotificationIdByDeviceId(deviceId, userId);
            if (resultPushNotification == null)
            {
                return Result<PushNotificationResponseDeviceData>.Failure("Push Notification Data not found");
            }

            PushNotificationResponseDeviceData pushNotificationData = new(resultPushNotification.IsTurnOnNotification);
            return Result<PushNotificationResponseDeviceData>.Success(pushNotificationData);
        }

        private async Task<Result> SendPushNotificationToApple(string deviceToken, PushNotificationBody notificationInfo)
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

        private async Task<Result> SendPushNotificationToAndroid(string deviceToken, PushNotificationBody notificationInfo)
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
        private async Task SendNotification(int userId, PushNotificationBody notificationBody)
        {
            List<DeviceDataResponse> result = await pushNotificationRepo.FetchEnabledUserDeviceDataById(userId);

            foreach (var deviceData in result)
            {
                string deviceToken = deviceData.Token;
                if (deviceData.OS == (int)DeviceOperationSystemType.Ios)
                {
                    await SendPushNotificationToApple(deviceToken, notificationBody);
                }

                if (deviceData.OS == (int)DeviceOperationSystemType.Android)
                {
                    await SendPushNotificationToAndroid(deviceToken, notificationBody);
                }
            }
        }

        public async Task<Result> NotificationForNewMessage(int userId, string name, string message, int chatId)
        {
            PushNotificationBody notificationBody = new PushNotificationBody()
            {
                Title = $"{name} send you a message",
                Body = message,
                Route = "Chats",
                ChatId = chatId,
            };

            await SendNotification(userId, notificationBody);

            return Result.Success();
        }

        public async Task<Result> NotificationForNewCreateChat(int userId, string name, int chatId)
        {
            PushNotificationBody notificationBody = new PushNotificationBody()
            {
                Title = "New Created chat",
                Body = $"{name} created chat with you",
                Route = "Chats",
                ChatId = chatId,
            };

            await SendNotification(userId, notificationBody);

            return Result.Success();
        }

        public async Task<Result> NotificationForNewFriendshipRequest(int userId, string name)
        {
            PushNotificationBody notificationBody = new PushNotificationBody()
            {
                Title = "New Request",
                Body = $"{name} you send new friendship request",
                Route = "Requests",
            };

            await SendNotification(userId, notificationBody);

            return Result.Success();
        }
        public async Task<Result> NotificationForAcceptFriendship(int userId, string name)
        {
            PushNotificationBody notificationBody = new PushNotificationBody()
            {
                Title = "Accepted request",
                Body = $"{name} accepted your friend request",
                Route = "Contacts",
            };

            await SendNotification(userId, notificationBody);

            return Result.Success();
        }
        public async Task<Result> NotificationForRejectedFriendship(int userId, string name)
        {
            PushNotificationBody notificationBody = new PushNotificationBody()
            {
                Title = "Rejected request",
                Body = $"{name} reject your friend request",
                Route = "RejectedRequests",
            };

            await SendNotification(userId, notificationBody);

            return Result.Success();
        }
    }
}