using System.ComponentModel.DataAnnotations;

namespace WebAPI.Models.User.Request
{
	public record AuthRequestModel(
		[Required] string Email,
		[Required] string Password
	);
}
