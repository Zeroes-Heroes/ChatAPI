using Database.Context;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Services.Chat.Interface;
using Services.Chat.Service;
using Services.DeviceNotificationConfig.Interface;
using Services.DeviceNotificationConfig.Service;
using Services.Friendship.Interface;
using Services.Friendship.Service;
using Services.Hubs.Resolvers;
using Services.NotificationDispatch.Interface;
using Services.NotificationDispatch.Service;
using Services.Presence.Interface;
using Services.Presence.Service;
using Services.Keys;
using Services.Keys.Interface;
using Services.Keys.Service;
using Services.Recovery.Interface;
using Services.Recovery.Service;
using Services.Sessions.Authorization;
using Services.Sessions.Interface;
using Services.Sessions.Service;
using Services.Hubs.Filters;
using Services.Repositories.Chat.Interface;
using Services.Repositories.Chat.Repository;
using Services.Repositories.DeviceNotificationConfig.Interface;
using Services.Repositories.DeviceNotificationConfig.Repository;
using Services.Repositories.Friendship.Interface;
using Services.Repositories.Friendship.Repository;
using Services.Repositories.Resources.Interface;
using Services.Repositories.Resources.Repository;
using Services.Repositories.User.Interface;
using Services.Repositories.User.Repository;
using Services.Resources.Interfaces;
using Services.Resources.Service;
using Services.Token.Interface;
using Services.Token.Service;
using Services.User.Interface;
using Services.User.Service;
using Services.Utilities.Models;
using System.Security.Claims;
using System.Text;
using Twilio;

namespace Services.Extensions;

public static class ServiceCollectionExtension
{
	public static IServiceCollection ConfigureAppSettings(this IServiceCollection services, IConfiguration config)
		=> services
			.Configure<AppSettings>(config)
			.Configure<NotificationSettings>(config)
			// Stage 05: read-only mounted master-secret directory for the key-protection module
			// (Services/Keys, stage 04) and its session-replay-cache sibling (Services/Sessions). Per
			// docs/sync-contract-v1.md: "Master secret is mounted read-only outside PostgreSQL" - only
			// the directory path and current-version name are configuration, never the key bytes
			// themselves. Bound from a nested "MasterSecret" section (env vars
			// MasterSecret__DirectoryPath / MasterSecret__CurrentVersion) rather than flat top-level
			// keys like AppSettings, to avoid colliding with other top-level configuration names.
			.Configure<MasterSecretOptions>(config.GetSection("MasterSecret"));

	/// <summary>
	/// Adds and configures the swagger.
	/// </summary>
	/// <param name="services">IServiceCollection</param>
	/// <returns>IServiceCollection in order to to be able to chain the services.</returns>
	public static IServiceCollection AddSwagger(this IServiceCollection services)
	{
		services.AddSwaggerGen(options =>
		{
			options.SwaggerDoc("v1", new OpenApiInfo { Title = "Swagger", Version = "v1" });
			options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
			{
				Description = "JWT Authorization header using the Bearer scheme.",
				Type = SecuritySchemeType.Http,
				Scheme = "bearer"
			});
			options.AddSecurityRequirement(new OpenApiSecurityRequirement
			{
				{
					new OpenApiSecurityScheme
					{
						Reference = new OpenApiReference
						{
							Type = ReferenceType.SecurityScheme,
							Id = "Bearer"
						}
					},
					new List<string>()
				}
			});
		});

		return services;
	}

	/// <summary>
	/// Configures and adds the database connection.
	/// </summary>
	/// <param name="services">IServiceCollection</param>
	/// <param name="configuration">IConfiguration</param>
	/// <returns>A reference to IServiceCollection</returns>
	public static IServiceCollection AddDatabase(this IServiceCollection services, IConfiguration configuration) =>
		services.AddDbContextPool<AppDbContext>(options =>
		{
			string host = configuration["Host"];
			string port = configuration["Port"];
			string username = configuration["Username"];
			string password = configuration["Password"];
			string database = configuration["Database"];
			string includeErrorDetails = configuration["IncludeErrorDetails"];
			string connectionString = $"Host={host};Port={port};Username={username};Password={password};Database={database};Include Error Detail={includeErrorDetails}";

			options
			.UseNpgsql(connectionString)
			.ConfigureWarnings(w => w.Throw(RelationalEventId.MultipleCollectionIncludeWarning));
		});

	public static IServiceCollection AddCaching(this IServiceCollection services, IConfiguration configuration)
	{
		string? redisConnectionString = configuration["RedisConnection"];
		return services.AddStackExchangeRedisCache(options =>
		{
			options.Configuration = redisConnectionString;
		});
	}

	/// <summary>
	/// Adds and configures the authentication, JwtBearer and authorization services.
	/// </summary>
	/// <param name="services">IServiceCollection</param>
	/// <param name="configuration">IConfiguration</param>
	/// <returns>IServiceCollection</returns>
	public static IServiceCollection AddAuthenticationConfigured(this IServiceCollection services, IConfiguration configuration)
	{
		services.AddAuthentication(options =>
		{
			options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
			options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
			options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
		})
		.AddJwtBearer(o =>
		{
			o.TokenValidationParameters = new TokenValidationParameters
			{
				ValidIssuer = configuration["TokenIssuer"],
				ValidAudience = configuration["TokenAudience"],
				IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["TokenSigningKey"]!)),
				ValidateIssuer = true,
				ValidateAudience = true,
				ValidateLifetime = true,
				ValidateIssuerSigningKey = true
			};
		});

		services.AddSingleton<IAuthorizationHandler, BootstrapAuthorizationHandler>();
		services.AddScoped<IAuthorizationHandler, AppSessionAuthorizationHandler>();

		return services.AddAuthorization(options =>
		{
			// Stage 05: every plain [Authorize] (no named Policy) - every existing controller and
			// BaseHub - now also requires AppSessionRequirement, i.e. an "app"-purpose token whose
			// session/device/account are currently trusted right now (Services/Sessions/Interface/ISessionTrustChecker.cs),
			// not merely a validly-signed, unexpired JWT. This is what stops the legacy bootstrap
			// token (phone+SMS+PIN only, no device proof) from remaining an unrestricted-access bypass:
			// it never carries an "app" token_purpose claim, so it fails this requirement's very first
			// check. The two device-enrollment endpoints use the separate, narrower "Bootstrap" policy
			// instead (AuthorizationController's SMS endpoints stay [AllowAnonymous], unaffected).
			options.DefaultPolicy = new AuthorizationPolicyBuilder()
				.RequireAuthenticatedUser()
				.RequireClaim(ClaimTypes.NameIdentifier)
				.AddRequirements(AppSessionRequirement.Instance)
				.Build();

			options.AddPolicy("Bootstrap", policy => policy
				.RequireAuthenticatedUser()
				.RequireClaim(ClaimTypes.NameIdentifier)
				.AddRequirements(BootstrapRequirement.Instance));
		});
	}

	/// <summary>
	/// Adds the services that the appliaction uses.
	/// </summary>
	/// <param name="services">IServiceCollection</param>
	/// <param name="configuration">IConfiguration</param>
	/// <returns>IServiceCollection</returns>
	public static IServiceCollection AddServices(this IServiceCollection services) =>
		services
			.AddTransient<ITokenService, TokenService>()
			.AddScoped<IAppleService, AppleService>()
			.AddScoped<IAppleTokenService, AppleTokenService>()
			.AddScoped<IUserRepository, UserRepository>()
			.AddScoped<IFriendshipRepository, FriendshipRepository>()
			.AddScoped<IUserService, UserService>()
			.AddScoped<IFriendshipService, FriendshipService>()
			.AddScoped<IResourceService, ResourcesService>()
			.AddScoped<IResourceRepository, ResourceRepository>()
			.AddScoped<IChatService, ChatService>()
			.AddScoped<IChatRepository, ChatRepository>()
			.AddScoped<IDeviceNotificationConfig, DeviceNotificationConfigService>()
			.AddScoped<IDeviceNotificationConfigRepository, DeviceNotificationConfigRepository>()
			.AddScoped<INotificationDispatch, NotificationDispatchService>()
			.AddScoped<IPresenceService, PresenceService>()
			// Stage 05 device/session module (Services/Sessions). Every registration below is
			// stateless with respect to AppDbContext - every method that needs one takes it as an
			// explicit parameter from the caller's own scope (matching Services/Keys's "caller owns
			// the transaction" convention) - except ISessionTrustChecker, which is the sole piece that
			// injects AppDbContext directly (it has no per-call caller to supply one: it is invoked
			// from the authorization handler/hub filter) and so must be Scoped, not Singleton.
			.AddSingleton<TimeProvider>(TimeProvider.System)
			.AddSingleton<IMasterSecretProvider, FileMasterSecretProvider>()
			.AddSingleton<IReplayPayloadProtector, AesGcmReplayPayloadProtector>()
			.AddSingleton<IDeviceProofVerifier, DeviceProofVerifier>()
			.AddSingleton<IDeviceChallengeService, DeviceChallengeService>()
			.AddSingleton<ISessionTokenIssuer, SessionTokenIssuer>()
			.AddSingleton<IRefreshTokenService, RefreshTokenService>()
			.AddSingleton<IDeviceTrustService, DeviceTrustService>()
			.AddSingleton<ISyncAccountProvisioningService, SyncAccountProvisioningService>()
			.AddSingleton<IDeviceEnrollmentCoordinator, DeviceEnrollmentCoordinator>()
			// Stage 06 (plan/06-recovery.md). The stage-04 key-protection module gets its first
			// production caller here (the recovery envelope), so it is registered now. The envelope
			// service injects the Scoped ISessionTrustChecker for its commit-time authority check and is
			// therefore Scoped itself; the approval service and recovery coordinator stay stateless
			// Singletons like their stage-05 siblings.
			.AddSingleton<IMessageKeyProtector, AesGcmMessageKeyProtector>()
			.AddScoped<IRecoveryEnvelopeService, RecoveryEnvelopeService>()
			.AddScoped<IRecoveryEnrollmentCoordinator, RecoveryEnrollmentCoordinator>()
			.AddSingleton<IDeviceApprovalService, DeviceApprovalService>()
			.AddScoped<IAccountContextBoundary, AccountContextBoundary>()
			.AddSingleton<ISessionRefreshCoordinator, SessionRefreshCoordinator>()
			.AddSingleton<IReplayPayloadCleanupService, ReplayPayloadCleanupService>()
			// Singleton: one in-memory, per-replica registry of currently-open hub connections for the
			// whole lifetime of the process - see ISessionConnectionRegistry's own doc comment.
			.AddSingleton<ISessionConnectionRegistry, SessionConnectionRegistry>()
			.AddSingleton<ISessionRevocationNotifier, SessionRevocationNotifier>()
			.AddScoped<ISessionTrustChecker, SessionTrustChecker>()
			// Periodic bounded replay-payload cleanup + cross-replica idle-connection revalidation - see
			// SessionMaintenanceHostedService's own doc comment for why these two small, unrelated jobs
			// share one timer loop instead of being two near-identical hosted services.
			.AddHostedService<SessionMaintenanceHostedService>()
			.AddSignalR(options =>
				{
					// Re-checks current trust on every hub invocation (including OnConnectedAsync) -
					// see Services/Hubs/Filters/SessionGuardHubFilter.cs's own doc comment for why
					// [Authorize]'s connect-time-only check is not enough on its own.
					options.AddFilter<SessionGuardHubFilter>();
				})
				 .AddMessagePackProtocol(options =>
				 {
					 options.SerializerOptions = MessagePackSerializerOptions.Standard
						 .WithResolver(CompositeResolver.Create(
							  CustomResolver.Instance,
							  ContractlessStandardResolver.Instance,
							  StandardResolver.Instance));
				 })
			.Services
			.AddScoped<SessionGuardHubFilter>();

	public static IServiceCollection InitializeTwilio(this IServiceCollection service, IConfiguration configuration)
	{
		TwilioClient.Init(configuration["TwillioAccountSid"], configuration["TwillioAuthToken"]);
		return service;
	}

	public static IServiceCollection InitializeFirebase(this IServiceCollection services, IConfiguration configuration)
	{
		string? androidPrivateKeyPath = configuration["AndroidPrivateKeyPath"];

		if (string.IsNullOrWhiteSpace(androidPrivateKeyPath))
		{
			Console.Error.WriteLine("[WARNING] AndroidPrivateKeyPath is not configured. Android push notifications will be disabled.");
			return services;
		}

		if (!File.Exists(androidPrivateKeyPath))
		{
			Console.Error.WriteLine($"[WARNING] Firebase credentials file not found at path: '{androidPrivateKeyPath}'. Android push notifications will be disabled.");
			return services;
		}

		try
		{
			FirebaseApp.Create(new AppOptions()
			{
				Credential = GoogleCredential.FromFile(androidPrivateKeyPath)
			});
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[ERROR] Firebase initialization failed: {ex.Message}");
			Console.Error.WriteLine($"[ERROR] Android push notifications will be disabled. Exception: {ex}");
		}
		return services;
	}
}
