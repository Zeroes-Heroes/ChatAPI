using Database.Entities;
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
    }
}