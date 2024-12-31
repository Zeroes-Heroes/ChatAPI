using Database.Context;
using Database.Seeders;
using Database.Seeders.Interfaces;
using Database.Seeders.Services;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Services.Chat.Interface;
using Services.Chat.Service;
using Services.Friendship.Interface;
using Services.Friendship.Service;
using Services.Hubs.Resolvers;
using Services.PushNotification.Interface;
using Services.PushNotification.Service;
using Services.Repositories.Chat.Interface;
using Services.Repositories.Chat.Repository;
using Services.Repositories.Friendship.Interface;
using Services.Repositories.Friendship.Repository;
using Services.Repositories.OperationSystem.Interface;
using Services.Repositories.OperationSystem.Repository;
using Services.Repositories.PushNotification.Interface;
using Services.Repositories.PushNotification.Repository;
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
		=> services.Configure<AppSettings>(config);

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

		return services.AddAuthorization(options =>
		{
			options.DefaultPolicy = new AuthorizationPolicyBuilder()
				.RequireAuthenticatedUser()
				.RequireClaim(ClaimTypes.NameIdentifier)
				.Build();
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
			.AddTransient<IAppleTokenService, AppleTokenService>()
			.AddHttpClient("ApplePushNotificationClient", client =>
			{
				client.BaseAddress = new Uri("https://api.example.com/");
			})
			.Services
			.AddTransient<IAppleService, AppleService>()
			.AddScoped<IUserRepository, UserRepository>()
			.AddScoped<IFriendshipRepository, FriendshipRepository>()
			.AddScoped<IUserService, UserService>()
			.AddScoped<IFriendshipService, FriendshipService>()
			.AddScoped<IResourceService, ResourcesService>()
			.AddScoped<IResourceRepository, ResourceRepository>()
			.AddScoped<IChatService, ChatService>()
			.AddScoped<IChatRepository, ChatRepository>()
			.AddScoped<IPushNotification, PushNotificationService>()
			.AddScoped<IPushNotificationRepository, PushNotificationRepository>()
			.AddScoped<IOperationSystemRepository, OperationSystemRepository>()
			.AddTransient<ISeeder, OperationSystemSeeder>()
			.AddTransient<SeederRunner>()
			.AddSignalR()
				 .AddMessagePackProtocol(options =>
				 {
					 options.SerializerOptions = MessagePackSerializerOptions.Standard
						 .WithResolver(CompositeResolver.Create(
							  CustomResolver.Instance,
							  ContractlessStandardResolver.Instance,
							  StandardResolver.Instance));
				 })
			.Services;

	public static IServiceCollection InitializeTwilio(this IServiceCollection service, IConfiguration configuration)
	{
		TwilioClient.Init(configuration["TwillioAccountSid"], configuration["TwillioAuthToken"]);
		return service;
	}

	public static IServiceCollection InitializeFirebase(this IServiceCollection services, IConfiguration configuration)
	{
		// TODO: Review the try {} catch block to either improve its error handling logic or remove it if unnecessary
		try
		{
			string AndroidPrivateKeyPath = configuration["AndroidPrivateKeyPath"];
			FirebaseApp.Create(new AppOptions()
			{
				Credential = GoogleCredential.FromFile(AndroidPrivateKeyPath)
			});
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Firebase initialization error: {ex.Message}");
		}
		return services;
	}
}
