namespace Services.Utilities.Statics;

public static class CacheKeys
{
	public const string AccessToken = "{0}_access_token";
	public const string RefreshToken = "{0}_refresh_token";
	public const string ChatEntered = "{0}_chat_entered_{1}";
	public const string ConnectionEstablished = "{0}_connection_established";
	public const string ApplePushNotificationToken = "ApplePushNotificationToken"; // Why is used {0}?
}
