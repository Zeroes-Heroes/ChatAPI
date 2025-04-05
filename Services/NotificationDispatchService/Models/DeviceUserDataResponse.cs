using Database.Enums;

namespace Services.NotificationDispatchService.Models;

public class DeviceUserDataResponse
{
    public OperatingSystemType OS { get; set; }
    public string Token { get; set; }
    public bool IsNotificationEnabled { get; set; }
    public int UserId { get; set; }
};