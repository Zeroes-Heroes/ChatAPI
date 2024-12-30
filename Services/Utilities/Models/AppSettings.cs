namespace Services.Utilities.Models;

public class AppSettings
{
	public string ASPNETCORE_ENVIRONMENT { get; set; }
	public string TokenSigningKey { get; set; }
	public string TokenIssuer { get; set; }
	public string TokenAudience { get; set; }
	public string TwillioAuthToken { get; set; }
	public string TwillioAccountSid { get; set; }
	public string TwillioVerificationSid { get; set; }
	public string ApplePrivateKeyPath { get; set; }
	public string AppleKeyId { get; set; }
	public string AppleTeamId { get; set; }
	public string ApplePushNotificationUrl { get; set; }
	public string AppleBundleId { get; set; }
	public string AndroidPrivateKeyPath { get; set; }
}
