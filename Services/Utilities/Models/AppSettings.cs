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
}
