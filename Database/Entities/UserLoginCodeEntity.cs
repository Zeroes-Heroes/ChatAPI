using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Database.Entities;

public class UserLoginCodeEntity
{
	public UserLoginCodeEntity(int userDeviceId, Guid secretLoginCode)
	{
		UserDeviceId = userDeviceId;
		SecretLoginCode = secretLoginCode;
	}

	[Key]
	public int UserDeviceId { get; set; }
	public Guid SecretLoginCode { get; set; }

	[ForeignKey(nameof(UserDeviceId))]
	public virtual UserDeviceEntity? UserDevice { get; set; }
}
