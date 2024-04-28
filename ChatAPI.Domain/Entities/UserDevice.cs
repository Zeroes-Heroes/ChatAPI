using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ChatAPI.Domain.Entities
{
	public class UserDevice
	{
		public int Id { get; set; }

		[Required]
		public int UserId { get; set; }

		[Required]
		public required string DeviceId { get; set; }

		[Required]
		public bool IsVerified { get; set; }

		[Required]
		public DateTime CreatedOn { get; set; }

		[ForeignKey(nameof(UserId))]
		public virtual required User User { get; set; }
	}
}