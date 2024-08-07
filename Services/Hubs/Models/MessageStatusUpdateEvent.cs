using System.Globalization;

namespace Services.Hubs.Models;

public class MessageStatusUpdateEvent
{
	public MessageStatusUpdateEvent(int chatId, int receiverId, int status, DateTime timestamp)
	{
		ChatId = chatId;
		ReceiverId = receiverId;
		Status = status;
		Timestamp = timestamp.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);
	}

	public MessageStatusUpdateEvent()
	{

	}

	public int ChatId { get; set; }
	public int ReceiverId { get; set; }
	public int Status { get; set; }
	public string Timestamp { get; set; }
}
