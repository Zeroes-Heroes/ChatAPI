using System.Globalization;

namespace Services.Hubs.Models
{
	public class NewMessageEvent
	{
		public NewMessageEvent(int id, int senderUserId, int chatId, string content, DateTime createdAt)
		{
			Id = id;
			SenderUserId = senderUserId;
			ChatId = chatId;
			Content = content;
			CreatedAt = createdAt.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);
		}

		public NewMessageEvent()
		{

		}

		public int Id { get; set; }
		public int SenderUserId { get; set; }
		public int ChatId { get; set; }
		public string Content { get; set; }
		public string CreatedAt { get; set; }

	}
}
