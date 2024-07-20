namespace Services.Hubs.Models;

public class SendMessage
{
	public string Content { get; set; }
	public int ChatId { get; set; }
	public int TempId { get; set; }
}
