namespace Services.Hubs.Models
{
	public record NewMessageEvent(int Id, int SenderUserId, int ChatId, string Content, DateTime CreatedAt);
}
