using System.Text.Json.Serialization;

namespace Services.NotificationDispatch.Models;

public class AppleNotificationPayload
{
	[JsonPropertyName("aps")]
	public ApplePushNotification Aps { get; set; }

	// Custom fields must be at root level, NOT inside "aps"
	[JsonPropertyName("route")]
	public string Route { get; set; }

	[JsonPropertyName("chatId")]
	public string ChatId { get; set; }
}

public class ApplePushNotification
{
	[JsonPropertyName("alert")]
	public Alert Alert { get; set; }

	[JsonPropertyName("sound")]
	public string Sound { get; set; }
}

public class Alert
{
	[JsonPropertyName("title")]
	public string Title { get; set; }

	[JsonPropertyName("body")]
	public string Body { get; set; }
}
