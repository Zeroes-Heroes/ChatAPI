using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ChatAPI.Domain.Enums;

namespace ChatAPI.Domain.Entities
{
	public class Friendship
	{
		public int Id { get; set; }

		[Required]
		public int SenderUserId { get; set; }

		[Required]
		public int TargetUserId { get; set; }

		[Required]
		public FriendshipStatus Status { get; set; }

		[Required]
		public DateTime CreatedOn { get; set; }


		[ForeignKey(nameof(SenderUserId))]
		public virtual User? Sender { get; set; }

		[ForeignKey(nameof(TargetUserId))]
		public virtual User? Target { get; set; }


		public Friendship(int senderUserId, int targetUserId)
		{
			SenderUserId = senderUserId;
			TargetUserId = targetUserId;
			Status = FriendshipStatus.Pending;
			CreatedOn = DateTime.UtcNow;
		}
	}
}
