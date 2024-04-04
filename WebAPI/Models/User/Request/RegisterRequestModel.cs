using System.ComponentModel.DataAnnotations;

namespace WebAPI.Models.User.Request
{
	public record RegisterRequestModel(
		[Required] string Email,
		[Required] string Password,
		[Required] string Phone);
}
