using Database.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Database.Entities;

public class FriendshipEntity
{
	public FriendshipEntity(int senderUserId, int targetUserId)
	{
		SenderUserId = senderUserId;
		TargetUserId = targetUserId;
		Status = FriendshipStatus.Pending;
		CreatedOn = DateTime.UtcNow;
	}

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
	public virtual UserEntity? Sender { get; set; }

	[ForeignKey(nameof(TargetUserId))]
	public virtual UserEntity? Target { get; set; }
}
