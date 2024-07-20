using Microsoft.Extensions.Configuration;

namespace Services.Utilities;

public static class EnvironmentHelper
{
	private static IConfiguration Configuration => ServiceLocator.GetService<IConfiguration>();
	private static readonly string Environment = Configuration["ASPNETCORE_ENVIRONMENT"];

	public static bool IsProduction() => Environment == "Production";
	public static bool IsStaging() => Environment == "Staging";
	public static bool IsDevelopment() => Environment == "Development";
}
