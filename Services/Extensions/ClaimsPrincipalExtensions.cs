using System.Security.Claims;

namespace Services.Extensions;

public static class ClaimsPrincipalExtensions
{
	public static int Id(this ClaimsPrincipal user)
	{
		string? nameIdentifier = user.FindFirstValue(ClaimTypes.NameIdentifier);
		return int.TryParse(nameIdentifier, out int userId)
		   ? userId
		   : throw new ArgumentNullException("Could not extract user id from token");
	}
}