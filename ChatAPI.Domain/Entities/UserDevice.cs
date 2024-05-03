using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ChatAPI.Domain.Entities
{
	public class UserDevice
	{
		public UserDevice(string deviceId)
		{
			DeviceId = deviceId;
			CreatedOn = DateTime.UtcNow;
		}

		public UserDevice(User user, string deviceId)
		{
			User = user;
			DeviceId = deviceId;
			IsVerified = false;
			CreatedOn = DateTime.UtcNow;
		}

		public int Id { get; set; }

		[Required]
		public int UserId { get; set; }

		[Required]
		public string DeviceId { get; set; }

		[Required]
		public bool IsVerified { get; set; }

		[Required]
		public DateTime CreatedOn { get; set; }

		[ForeignKey(nameof(UserId))]
		public virtual User? User { get; set; }
		public virtual UserLoginCode? UserLoginCode { get; set; }
	}
}