namespace Services.Chat.Models.CreateChat;

public class CreateChatRequest
{
	public int[] UserIds { get; set; }
	public string? Name { get; set; }
}
