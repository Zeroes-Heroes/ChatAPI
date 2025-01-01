namespace Services.PushNotification.Models;

public class DeviceDataResponse
{
    public int OS { get; set; }
    public string Token { get; set; }
    public bool IsTurnOnNotification { get; set; }
};