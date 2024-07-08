using System.ComponentModel.DataAnnotations;
using PhoneAttribute = Services.Utilities.Attributes.PhoneAttribute;

namespace Services.User.Models;

public class UserRegisterDTO
{
	[Required, StringLength(50), Phone]
	public required string Phone { get; set; }

	[Required, StringLength(100)]
	public required string Name { get; set; }

	[Required, StringLength(100)]
	public required string DeviceId { get; set; }
}
