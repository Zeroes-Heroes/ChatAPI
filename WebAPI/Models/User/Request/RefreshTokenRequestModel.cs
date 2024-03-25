using System.ComponentModel.DataAnnotations;

namespace WebAPI.Models.User.Request
{
	public record RefreshTokenRequestModel([property: Required] string RefreshToken);
}
