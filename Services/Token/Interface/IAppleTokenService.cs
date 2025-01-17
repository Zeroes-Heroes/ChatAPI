namespace Services.Token.Interface;

public interface IAppleTokenService
{
	Task<string> GetPushNotificationToken();
}