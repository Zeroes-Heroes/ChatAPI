using Services.DeviceNotificationConfig.Models;
using Services.Utilities;

namespace Services.DeviceNotificationConfig.Interface
{
    public interface IDeviceNotificationConfig
    {
        Task<Result> SubscribeDeviceForNotification(DeviceNotificationDTO deviceData, int userId, string deviceId);
        Task<Result> ChangeDeviceNotificationStatus(int userId, string deviceId, ChangeStatusRequest request);
        Task<Result<DeviceNotificationResponseDeviceData>> GetDeviceDataForNotification(string deviceId, int userId);
    }
}