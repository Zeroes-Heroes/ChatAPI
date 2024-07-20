namespace Services.Hubs.Models
{
	public class NewMessageCreatedEvent(int Id, int ChatId, int TempId, string CreatedAt)
	{
		public NewMessageCreatedEvent(int Id, int ChatId, int TempId, DateTime CreatedAt)
			: this(Id, ChatId, TempId, CreatedAt.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'")) { }
	}
}
