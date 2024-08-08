using Database.Enums;

namespace Services.Hubs.Models;

public class MessageStatusUpdateConfirmation
{
	public int ChatId { get; set; }
	public MessageStatus Status { get; set; }
}
