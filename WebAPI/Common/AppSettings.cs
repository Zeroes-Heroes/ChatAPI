namespace WebAPI.Common
{
	public class AppSettings
	{
		public required string TokenSigningKey { get; set; }
		public required string TokenIssuer { get; set; }
		public required string TokenAudience { get; set; }
	}
}
