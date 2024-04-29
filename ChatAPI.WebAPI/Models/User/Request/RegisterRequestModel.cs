using System.ComponentModel.DataAnnotations;

namespace ChatAPI.WebAPI.Models.User.Request
{
	public record RegisterRequestModel(
		[Required] string Email,
		[Required] string Password,
		[Required] string Phone);
}
