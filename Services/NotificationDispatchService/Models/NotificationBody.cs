namespace Services.NotificationDispatch.Models;

public class NotificationPayload
{
    public string Title { get; set; }
    public string Body { get; set; }
    public string Route { get; set; }
    public int? ChatId { get; set; }
};