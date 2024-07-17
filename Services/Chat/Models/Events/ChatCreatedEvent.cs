namespace Services.Chat.Models.Events
{
	public record ChatCreatedEvent(int Id, string Name, int[] UserIds);
}
