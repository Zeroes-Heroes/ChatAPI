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

            bool isDeviceTokenExist = await pushNotificationRepo.DoesDeviceTokenExist(deviceData.Token, userId);
            if (isDeviceTokenExist)
            {
                return Result.Failure("Something went wrong with your device token");
            };

            int userDeviceId = await userRepo.GetDeviceByDeviceId(deviceId);

            PushNotificationEntity notificationEntity = new(operatingSystemType, deviceData.Token, IsNotificationEnabled: true, userId, userDeviceId);

            pushNotificationRepo.AddDeviceData(notificationEntity);

            await pushNotificationRepo.SaveChangesAsync();

            return Result.Success();
        }

        public async Task<Result> ChangePushNotificationStatus(int userId, string deviceId, ChangeStatusRequest request)
        {
            bool result = await pushNotificationRepo.UpdateDeviceData(userId, deviceId, request.IsNotificationEnabled);
            if (!result)
            {
                return Result.Failure("No data found or new record is the same as old record");
            }

            return Result.Success();
        }

        public async Task<Result<PushNotificationResponseDeviceData>> GetPushNotificationData(string deviceId, int userId)
        {
            bool? isNotificationEnabled = await pushNotificationRepo.IsUserDeviceNotificationEnabled(deviceId, userId);
            if (isNotificationEnabled == null)
            {
                return Result<PushNotificationResponseDeviceData>.Failure("Push Notification Data not found");
            }

            PushNotificationResponseDeviceData pushNotificationData = new(isNotificationEnabled ?? false);
            return Result<PushNotificationResponseDeviceData>.Success(pushNotificationData);
        }
    }
}