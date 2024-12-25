namespace Services.PushNotification.Models;

public record PushNotificationDTO(string Token, string OS, string DeviceId);