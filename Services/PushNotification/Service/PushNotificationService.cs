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

            var deviceId = await userRepo.GetIdByDeviceId(deviceData.DeviceId);
            if (deviceId == null)
            {
                Result<PushNotificationResponse>.Failure("Device Id doesn't exists");
            }

            PushNotificationEntity notificationEntity = new(deviceData.OS, deviceData.Token, IsTurnOnNotification: true, UserId: userId, deviceId.Id);

            PushNotificationEntity result = await pushNotificationRepo.AddDeviceData(notificationEntity);

            PushNotificationResponse notificationId = new(result.Id);

            return Result<PushNotificationResponse>.Success(notificationId);
        }

        public async Task<Result> UnsubscribeForPushNotification(int notificationId)
        {
            PushNotificationEntity? result = await pushNotificationRepo.UpdateDeviceData(notificationId);
            if (result == null)
            {
                Result.Failure("No data found");
            }

            return Result.Success();
        }

        public async Task<Result<PushNotificationResponseDeviceData>> GetDeviceDataForPushNotification(string deviceData, int userId)
        {
            var result = await userRepo.GetIdByDeviceId(deviceData);
            if (result == null)
            {
                return Result<PushNotificationResponseDeviceData>.Failure("Device id is not found");
            }

            PushNotificationEntity? resultPushNotification = await pushNotificationRepo.GetPushNotificationIdByDeviceId(result.Id, userId);
            if (resultPushNotification == null)
            {
                return Result<PushNotificationResponseDeviceData>.Failure("Push Notification Data not found");
            }

            PushNotificationResponseDeviceData pushNotificationData = new(resultPushNotification.Id, resultPushNotification.IsTurnOnNotification);
            return Result<PushNotificationResponseDeviceData>.Success(pushNotificationData);
        }
    }
}