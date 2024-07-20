using Services.Hubs.Enums;

namespace Services.Hubs.Models;

public record MessageStatusUpdateEvent(int ChatId, int ReceiverId, MessageStatus Status, DateTime Timestamp);
