using Database.Entities;
using Services.PushNotification.Interface;
using Services.PushNotification.Models;
using Services.Repositories.OperationSystem.Interface;
using Services.Repositories.PushNotification.Interface;
using Services.Repositories.User.Interface;
using Services.Utilities;

namespace Services.PushNotification.Service
{
    public class PushNotificationService(IPushNotificationRepository pushNotificationRepo, IUserRepository userRepo, IOperationSystemRepository operationSystemRepo) : IPushNotification
    {
        public async Task<Result> SubscribeForPushNotification(PushNotificationDTO deviceData, int userId, string deviceId)
        {
            bool checkDeviceTokenExists = await pushNotificationRepo.CheckDeviceTokenExists(deviceData, userId);
            if (checkDeviceTokenExists)
            {
                return Result.Failure("This Device token already exists");
            };

            var userDevice = await userRepo.GetDeviceByDeviceId(deviceId);
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

            PushNotificationResponseDeviceData pushNotificationData = new(resultPushNotification.IsTurnOnNotification);
            return Result<PushNotificationResponseDeviceData>.Success(pushNotificationData);
        }
    }
}