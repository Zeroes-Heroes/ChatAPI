using Database.Entities;
using Services.PushNotification.Interface;
using Services.PushNotification.Models;
using Services.Repositories.PushNotification.Interface;
using Services.Repositories.User.Interface;
using Services.Utilities;

namespace Services.PushNotification.Service
{
    public class PushNotificationService(IPushNotificationRepository pushNotificationRepo, IUserRepository userRepo) : IPushNotification
    {
        public async Task<Result<PushNotificationResponse>> SubscribeForPushNotification(PushNotificationDTO deviceData, int userId)
        {
            bool doesDeviceTokenExist = await pushNotificationRepo.DoesDeviceTokenExist(deviceData, userId);
            if (doesDeviceTokenExist)
            {
                return Result<PushNotificationResponse>.Failure("This Device token already exists");
            };

            var userDevice = await userRepo.GetIdByDeviceId(deviceData.DeviceId);
            if (userDevice == null)
            {
                Result<PushNotificationResponse>.Failure("Device Id doesn't exists");
            }

            PushNotificationEntity notificationEntity = new(deviceData.OS, deviceData.Token, IsTurnOnNotification: true, UserId: userId, userDevice.Id);

            PushNotificationEntity result = await pushNotificationRepo.AddDeviceData(notificationEntity);

            PushNotificationResponse notificationId = new(result.Id);

            return Result<PushNotificationResponse>.Success(notificationId);
        }

        public async Task<Result> ChangePushNotificationStatus(int userId, string deviceId, ChangeStatusRequest request)
        {
            PushNotificationEntity? result = await pushNotificationRepo.UpdateDeviceData(userId, deviceId, request.IsNotificationStatus);
            if (result == null)
            {
                Result.Failure("No data found or new record is the same with old record");
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
    }
}