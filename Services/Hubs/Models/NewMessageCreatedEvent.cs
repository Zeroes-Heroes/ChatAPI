using System.Globalization;

namespace Services.Hubs.Models
{
	public class NewMessageCreatedEvent
	{
		public NewMessageCreatedEvent(int id, int chatId, int tempId, DateTime createdAt)
		{
			Id = id;
			ChatId = chatId;
			TempId = tempId;
			CreatedAt = createdAt.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);
		}

		public NewMessageCreatedEvent()
		{

		}

		public int Id { get; set; }
		public int ChatId { get; set; }
		public int TempId { get; set; }
		public string CreatedAt { get; set; }
	}
}
