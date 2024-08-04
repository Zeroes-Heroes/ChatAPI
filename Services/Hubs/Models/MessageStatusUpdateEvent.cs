using Services.Hubs.Enums;

namespace Services.Hubs.Models;

public class MessageStatusUpdateEvent
{
	public MessageStatusUpdateEvent(int chatId, int receiverId, MessageStatus status, DateTime timestamp)
	{
		ChatId = chatId;
		ReceiverId = receiverId;
		Status = status;
		Timestamp = timestamp;
	}

	public MessageStatusUpdateEvent()
	{

	}

	public int ChatId { get; set; }
	public int ReceiverId { get; set; }
	public MessageStatus Status { get; set; }
	public DateTime Timestamp { get; set; }
}
