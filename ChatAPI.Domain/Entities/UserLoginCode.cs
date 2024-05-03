using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ChatAPI.Domain.Entities
{
	public class UserLoginCode
	{
		public UserLoginCode(int userDeviceId, Guid secretLoginCode)
		{
			UserDeviceId = userDeviceId;
			SecretLoginCode = secretLoginCode;
		}

		[Key]
		public int UserDeviceId { get; set; }
		public Guid SecretLoginCode { get; set; }

		[ForeignKey(nameof(UserDeviceId))]
		public virtual UserDevice? UserDevice { get; set; }
	}
}
