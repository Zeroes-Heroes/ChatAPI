using Database.Entities;
using Database.Enums;
using Services.PushNotification.Interface;
using Services.PushNotification.Models;
using Services.Repositories.PushNotification.Interface;
using Services.Repositories.User.Interface;
using Services.Utilities;

namespace Services.PushNotification.Service
{
    public class PushNotificationService(IPushNotificationRepository pushNotificationRepo, IUserRepository userRepo) : IPushNotification
    {
        public async Task<Result> SubscribeForPushNotification(PushNotificationDTO deviceData, int userId, string deviceId)
        {
            bool isExistingOperatingSystemCheck = Enum.TryParse(deviceData.OS, true, out OperatingSystemType operatingSystemType);
            if (!isExistingOperatingSystemCheck)
            {
                return Result.Failure($"This operating system {deviceData.OS} is not supported");
            }

            var userDevice = await userRepo.GetDeviceByDeviceId(deviceId);
            if (userDevice == null)
            {
                return Result.Failure("Device Id doesn't exists");
            }

            bool checkDeviceTokenExists = await pushNotificationRepo.CheckDeviceTokenExists(deviceData, userId);
            if (checkDeviceTokenExists)
            {
                return Result.Failure("This Device token already exists");
            };

            PushNotificationEntity notificationEntity = new(operatingSystemType, deviceData.Token, IsNotificationEnabled: true, UserId: userId, userDevice.Id);

            await pushNotificationRepo.AddDeviceData(notificationEntity);

            return Result.Success();
        }

        public async Task<Result> ChangePushNotificationStatus(int userId, string deviceId, ChangeStatusRequest request)
        {
            PushNotificationEntity? result = await pushNotificationRepo.UpdateDeviceData(userId, deviceId, request.IsNotificationEnabled);
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

            PushNotificationResponseDeviceData pushNotificationData = new(resultPushNotification.IsNotificationEnabled);
            return Result<PushNotificationResponseDeviceData>.Success(pushNotificationData);
        }
    }
}