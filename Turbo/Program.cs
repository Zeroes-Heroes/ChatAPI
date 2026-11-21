using Database.Context;
using Services.Extensions;
using Services.Hubs;
using Services.Utilities;

namespace Turbo;

public class Program
{
	/// <summary>
	/// Schema migrations are a separate, explicitly invoked step (plan 02): normal API startup
	/// never touches the schema, so multiple replicas never race to migrate concurrently and a
	/// new version's migrations can be applied, verified, and rolled out before any replica of
	/// that version starts serving traffic. Run this once, before the first replica of a new
	/// version starts, e.g. as a Kubernetes Job/initContainer or manually:
	///   dotnet Turbo.dll --migrate-only
	/// This only brings up configuration and the database connection - none of the application's
	/// other dependencies (Redis, Twilio, Firebase, SignalR) are initialized for this path.
	/// </summary>
	private const string MigrateOnlyArg = "--migrate-only";

	public static void Main(string[] args)
	{
		if (args.Contains(MigrateOnlyArg, StringComparer.OrdinalIgnoreCase))
		{
			RunMigrateOnly();
			return;
		}

		WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

		if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_DOCKER_COMPOSE")))
		{
			LoadDotEnv();
			builder.Configuration.AddEnvironmentVariables();
		}

		// Add services to the container
		builder.Services
			.AddCors(options =>
			{
				options.AddPolicy("AllowOrigin", builder =>
				{
					builder
					.SetIsOriginAllowed(_ => true)
					.AllowAnyMethod()
					.AllowAnyHeader()
					.AllowCredentials();
				});
			})
			.ConfigureAppSettings(builder.Configuration)
			.AddSwagger()
			.AddDatabase(builder.Configuration)
			.AddCaching(builder.Configuration)
			.AddAuthenticationConfigured(builder.Configuration)
			.AddAuthorization()
			.InitializeTwilio(builder.Configuration)
			.InitializeFirebase(builder.Configuration)
			.AddServices()
			.AddEndpointsApiExplorer()
			.AddControllers();

		WebApplication app = builder.Build();

		// Configure the HTTP request pipeline.
		if (app.Environment.IsDevelopment())
			app.UseSwaggerUI();
		else
			app.UseExceptionHandler("/error");

		ServiceLocator.Configure(app.Services);

		app.UseCors("AllowOrigin");

		app.UseHttpsRedirection();
		app.UseAuthentication();
		app.UseAuthorization();
		app.MapControllers();
		app.MapHub<BaseHub>("/chatHub");

		app.Run();
	}

	private static void RunMigrateOnly()
	{
		HostApplicationBuilder builder = Host.CreateApplicationBuilder();

		if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_DOCKER_COMPOSE")))
		{
			LoadDotEnv();
			builder.Configuration.AddEnvironmentVariables();
		}

		builder.Services.AddDatabase(builder.Configuration);

		using IHost host = builder.Build();
		using IServiceScope scope = host.Services.CreateScope();
		scope.ServiceProvider.GetRequiredService<AppDbContext>().ApplyMigrations();
		Console.WriteLine("Migrations applied.");
	}

	private static void LoadDotEnv()
	{
		string envFile = Path.Combine(Directory.GetCurrentDirectory(), "../.env");
		if (File.Exists(envFile))
		{
			DotNetEnv.Env.Load(envFile);
		}
	}
}
