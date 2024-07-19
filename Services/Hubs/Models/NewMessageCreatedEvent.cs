namespace Services.Hubs.Models
{
	public record NewMessageCreatedEvent(int Id, int ChatId, int TempId, DateTime CreatedAt);
}
