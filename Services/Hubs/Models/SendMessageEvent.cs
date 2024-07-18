namespace Services.Hubs.Models;

public class SendMessageEvent
{
	public string Message { get; set; }
	public int ChatId { get; set; }
	public int TempId { get; set; }
}
