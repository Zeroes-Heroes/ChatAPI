using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Database.Entities;

public class UserDeviceEntity
{
	public UserDeviceEntity(string deviceId)
	{
		DeviceId = deviceId;
		CreatedOn = DateTime.UtcNow;
	}

	public UserDeviceEntity(UserEntity user, string deviceId)
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
	public virtual UserEntity? User { get; set; }
	public virtual UserLoginCodeEntity? UserLoginCode { get; set; }
	public virtual DeviceNotificationConfigEntity? DeviceNotificationsConfig { get; set; }
}