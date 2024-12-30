namespace Services.Token.Interface;

public interface IAppleTokenService
{
	Task<string> GeneratePushNotificationToken();
	Task<string> GetPushNotificationToken();
}