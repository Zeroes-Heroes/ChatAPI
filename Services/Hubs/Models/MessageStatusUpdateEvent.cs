using Database.Enums;

namespace Services.Hubs.Models;

public class MessageStatusUpdateEvent
{
	public MessageStatusUpdateEvent(int chatId, int receiverId, MessageStatus status, DateTime timestamp, int messagesIds)
	{
		ChatId = chatId;
		ReceiverId = receiverId;
		Status = status;
		Timestamp = timestamp;
		MessageId = messagesIds;
	}

	public MessageStatusUpdateEvent()
	{

	}

	public int ChatId { get; set; }
	public int ReceiverId { get; set; }
	public int MessageId { get; set; }
	public MessageStatus Status { get; set; }
	public DateTime Timestamp { get; set; }
}
