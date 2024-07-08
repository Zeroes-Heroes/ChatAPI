namespace Services.Utilities.Models;

public class AppSettings
{
	public required string TokenSigningKey { get; set; }
	public required string TokenIssuer { get; set; }
	public required string TokenAudience { get; set; }
	public required string TwillioAuthToken { get; set; }
	public required string TwillioAccountSid { get; set; }
	public required string TwillioVerificationSid { get; set; }
}
