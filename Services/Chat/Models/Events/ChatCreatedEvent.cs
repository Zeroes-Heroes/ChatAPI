namespace Services.Chat.Models.Events
{
	public record ChatCreatedEvent(int ChatId, string? ChatName, int[] UserIds);
}
