using Database.Enums;

namespace Services.NotificationDispatch.Models;

public class DeviceData
{
    public OperatingSystemType OS { get; set; }
    public string Token { get; set; }
    public bool IsNotificationEnabled { get; set; }
};