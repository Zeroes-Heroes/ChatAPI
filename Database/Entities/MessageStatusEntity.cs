using System.ComponentModel.DataAnnotations.Schema;

namespace Database.Entities;

public class MessageStatusEntity
{
	public MessageStatusEntity(int messageId, int receiverId, int status, DateTime timeStamp)
	{
		MessageId = messageId;
		ReceiverId = receiverId;
		Status = status;
		TimeStamp = timeStamp;
	}

	public int MessageId { get; set; }
	public int ReceiverId { get; set; }
	public int Status { get; set; }
	public DateTime TimeStamp { get; set; }

	[ForeignKey(nameof(MessageId))]
	public MessageEntity Message { get; set; }

	[ForeignKey(nameof(ReceiverId))]
	public UserEntity Receiver { get; set; }
}
