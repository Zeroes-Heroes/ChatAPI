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

	public static string DeviceId(this ClaimsPrincipal user)
	{
		string? deviceId = user.FindFirstValue(CustomClaimTypes.DeviceId);
		return deviceId ?? throw new ArgumentNullException("Could not extract device id from token");
	}
}

public static class CustomClaimTypes
{
	// In our case, we need: "http://schemas.turbo.com/claims/deviceid". If everything before "/deviceId" is removed, 
	// an attempt to use the endpoint returns a 500 error. 
	public static readonly string DeviceId = "http://schemas.turbo.com/claims/deviceid";
}