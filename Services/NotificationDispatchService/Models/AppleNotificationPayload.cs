namespace Services.NotificationDispatch.Models;

public class AppleNotificationPayload
{
    public ApplePushNotification aps { get; set; }
}

public class ApplePushNotification
{
    public Alert Alert { get; set; }
    public string Sound { get; set; }
    public string Route { get; set; }
    public string ChatId { get; set; }
}

public class Alert
{
    public string Title { get; set; }
    public string Body { get; set; }
}