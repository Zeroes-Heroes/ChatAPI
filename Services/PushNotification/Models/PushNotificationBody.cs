namespace Services.PushNotification.Models;

public record PushNotificationBody(string Title, string Body, string Route, int? ChatId);