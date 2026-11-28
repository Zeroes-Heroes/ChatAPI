namespace Services.Hubs.Models;

public class NewMessageCreatedEvent
{
	public NewMessageCreatedEvent(int id, int chatId, int tempId, DateTime createdAt, int recipientCount = 0)
	{
		Id = id;
		ChatId = chatId;
		TempId = tempId;
		CreatedAt = createdAt;
		RecipientCount = recipientCount;
	}

	public NewMessageCreatedEvent()
	{

	}

	public int Id { get; set; }
	public int ChatId { get; set; }
	public int TempId { get; set; }
	public DateTime CreatedAt { get; set; }

	/// <summary>
	/// Stage 10: how many PEOPLE have the right to this message, with the author excluded
	/// ("Получателите се броят по хора, като подателят е изключен") - the denominator of its group
	/// proportions, decided by the server at the moment of acceptance. It is part of the ACK because no
	/// later event carries it and the author cannot derive it from a participant list: a member added
	/// afterwards is not a recipient of this message, and a removed one whose archive covers it still is.
	/// </summary>
	public int RecipientCount { get; set; }
}
