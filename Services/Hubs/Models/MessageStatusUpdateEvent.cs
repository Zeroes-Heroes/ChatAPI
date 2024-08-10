using Database.Enums;

namespace Services.Hubs.Models;

public class MessageStatusUpdateEvent
{
	public MessageStatusUpdateEvent(int chatId, int receiverId, MessageStatus status, DateTime timestamp, int[] messageIds)
	{
		ChatId = chatId;
		ReceiverId = receiverId;
		Status = status;
		Timestamp = timestamp;
		MessageIds = messageIds;
	}

	public MessageStatusUpdateEvent()
	{

	}

	public int ChatId { get; set; }
	public int ReceiverId { get; set; }
	public int[] MessageIds { get; set; }
	public MessageStatus Status { get; set; }
	public DateTime Timestamp { get; set; }
}
