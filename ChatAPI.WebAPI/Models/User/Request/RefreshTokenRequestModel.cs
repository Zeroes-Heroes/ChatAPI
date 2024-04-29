using System.ComponentModel.DataAnnotations;

namespace ChatAPI.WebAPI.Models.User.Request
{
	public record RefreshTokenRequestModel([property: Required] string RefreshToken);
}
