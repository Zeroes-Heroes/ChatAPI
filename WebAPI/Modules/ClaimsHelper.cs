using System.Security.Claims;

namespace WebAPI.Modules
{
	public static class ClaimsHelper
	{
		public static int GetUserId(this HttpContext context)
		{
			// Retrieve the user's claims from the HttpContext
			IEnumerable<Claim> userClaims = context.User.Claims;

			// Find the claim representing the user ID
			Claim? userIdClaim = userClaims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier);

			return userIdClaim != null && int.TryParse(userIdClaim.Value, out int userId)
				? userId
				: throw new ArgumentNullException("Could not extract user id from token");
		}
	}
}