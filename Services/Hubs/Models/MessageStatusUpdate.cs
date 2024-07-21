using Services.Hubs.Enums;

namespace Services.Hubs.Models
{
	public class MessageStatusUpdate
	{
		public int ChatId { get; set; }
		public int Status { get; set; }
	}
}
