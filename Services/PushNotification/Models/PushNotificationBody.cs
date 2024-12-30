namespace Services.PushNotification.Models;

public class PushNotificationBody
{
    public string Title { get; set; }
    public string Body { get; set; }
    public string Route { get; set; }
    public int? ChatId { get; set; }
};