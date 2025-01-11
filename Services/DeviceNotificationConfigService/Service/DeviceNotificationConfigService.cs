using Database.Entities;
using Database.Enums;
using Services.DeviceNotificationConfig.Interface;
using Services.DeviceNotificationConfig.Models;
using Services.Repositories.DeviceNotificationConfig.Interface;
using Services.Repositories.User.Interface;
using Services.Utilities;

namespace Services.DeviceNotificationConfig.Service
{
    public class DeviceNotificationConfigService(IDeviceNotificationConfigRepository deviceNotificationConfigRepo, IUserRepository userRepo) : IDeviceNotificationConfig
    {
        public async Task<Result> SubscribeDeviceForNotification(DeviceNotificationDTO deviceData, int userId, string deviceId)
        {
            bool isExistingOperatingSystemCheck = Enum.TryParse(deviceData.OS, true, out OperatingSystemType operatingSystemType);
            if (!isExistingOperatingSystemCheck)
            {
                return Result.Failure($"This operating system {deviceData.OS} is not supported");
            }

            bool doesTokenExistForUser = await deviceNotificationConfigRepo.DoesTokenExistForUser(deviceData.Token, userId);
            if (doesTokenExistForUser)
            {
                return Result.Failure("Something went wrong with your device token");
            };

            int userDeviceId = await userRepo.GetUserDeviceIdByDeviceId(deviceId);

            DeviceNotificationConfigEntity notificationEntity = new(operatingSystemType, deviceData.Token, IsNotificationEnabled: true, userId, userDeviceId);

            deviceNotificationConfigRepo.AddDeviceData(notificationEntity);

            await deviceNotificationConfigRepo.SaveChangesAsync();

            return Result.Success();
        }

        public async Task<Result> ChangeDeviceNotificationStatus(int userId, string deviceId, ChangeStatusRequest request)
        {
            bool result = await deviceNotificationConfigRepo.UpdateDeviceData(userId, deviceId, request.IsNotificationEnabled);
            if (!result)
            {
                return Result.Failure("No data found or the new record is the same as the old record");
            }

            await deviceNotificationConfigRepo.SaveChangesAsync();

            return Result.Success();
        }

        public async Task<Result<DeviceNotificationResponseDeviceData>> GetDeviceDataForNotification(string deviceId, int userId)
        {
            bool? isNotificationEnabled = await deviceNotificationConfigRepo.IsUserDeviceNotificationEnabled(deviceId, userId);
            if (isNotificationEnabled == null)
            {
                return Result<DeviceNotificationResponseDeviceData>.Failure("Notification data not found");
            }

            DeviceNotificationResponseDeviceData pushNotificationData = new(isNotificationEnabled ?? false);
            return Result<DeviceNotificationResponseDeviceData>.Success(pushNotificationData);
        }
    }
}