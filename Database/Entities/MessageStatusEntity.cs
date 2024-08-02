using System.ComponentModel.DataAnnotations.Schema;

namespace Database.Entities;

public class MessageStatusEntity
{
	public MessageStatusEntity(int messageId, int receiverId, int status, DateTime timestamp)
	{
		MessageId = messageId;
		ReceiverId = receiverId;
		Status = status;
		Timestamp = timestamp;
	}

    public MessageStatusEntity()
    {
			
    }

    public int MessageId { get; set; }
	public int ReceiverId { get; set; }
	public int Status { get; set; }
	public bool StatusUpdateDeliveryConfirmed{ get; set; }
	public DateTime Timestamp { get; set; }

	[ForeignKey(nameof(MessageId))]
	public MessageEntity Message { get; set; }

	[ForeignKey(nameof(ReceiverId))]
	public UserEntity Receiver { get; set; }
}
