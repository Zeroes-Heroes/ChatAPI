namespace Services.PushNotification.Models;

/// <summary>
/// <param name="Token" Represents a Data Transfer Object (DTO) that contains essential information about a device's notification settings. 
/// This includes the device's unique token, which is used for sending notifications,
/// <param name="OS" Operating system, which helps determine the appropriate API (Apple or Android) for notification delivery.
/// </summary>
public record PushNotificationDTO(string Token, string OS);