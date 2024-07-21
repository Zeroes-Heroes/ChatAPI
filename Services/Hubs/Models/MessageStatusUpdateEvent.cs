namespace Services.Hubs.Models;

public class MessageStatusUpdateEvent
{
	public MessageStatusUpdateEvent(int chatId, int receiverId, int status, DateTime timestamp)
	{
		ChatId = chatId;
		ReceiverId = receiverId;
		Status = status;
		Timestamp = timestamp.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'");
	}

	public MessageStatusUpdateEvent()
	{

	}

	public int ChatId { get; }
	public int ReceiverId { get; }
	public int Status { get; }
	public string Timestamp { get; }
}
