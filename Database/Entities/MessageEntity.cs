using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Database.Entities
{
	public class MessageEntity
	{
		public MessageEntity(string content, int senderId, int chatId, DateTime createdAt)
		{
			Content = content;
			SenderId = senderId;
			ChatId = chatId;
			CreatedAt = createdAt;
		}

		[Key]
		public int Id { get; set; }
		public string Content { get; set; }
		public int SenderId { get; set; }
		public int ChatId { get; set; }
		public DateTime CreatedAt { get; set; }

		[ForeignKey(nameof(ChatId))]
		public ChatEntity Chat { get; set; }

		public ICollection<MessageStatusEntity> MessageStatusEntities { get; set; } = [];
	}
}
