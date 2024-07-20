namespace Services.Hubs.Models
{
	public class NewMessageEvent(int Id, int SenderUserId, int ChatId, string Content, string CreatedAt)
	{
		public NewMessageEvent(int Id, int SenderUserId, int ChatId, string Content, DateTime CreatedAt)
			: this(Id, SenderUserId, ChatId, Content, CreatedAt.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'")) { }
	}
}
