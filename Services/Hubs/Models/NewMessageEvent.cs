namespace Services.Hubs.Models;

public class NewMessageEvent
{
	public NewMessageEvent(int id, int senderUserId, int chatId, string content, DateTime createdAt)
	{
		Id = id;
		SenderUserId = senderUserId;
		ChatId = chatId;
		Content = content;
		CreatedAt = createdAt;
	}

	public NewMessageEvent()
	{

	}

	public int Id { get; set; }
	public int SenderUserId { get; set; }
	public int ChatId { get; set; }
	public string Content { get; set; }
	public DateTime CreatedAt { get; set; }

}
