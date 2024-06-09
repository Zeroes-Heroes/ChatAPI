using ChatAPI.Application.UseCases.Abstractions.Repositories;
using ChatAPI.Application.UseCases.Abstractions.Services;
using ChatAPI.Application.UseCases.Implementations;
using ChatAPI.Persistence.Database;
using ChatAPI.Persistence.Repositories;
using ChatAPI.WebAPI.Common;
using ChatAPI.WebAPI.Modules.MessagePackProtocol;
using ChatAPI.WebAPI.Services.Authorization;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Security.Claims;
using System.Text;

namespace ChatAPI.WebAPI.Extensions
{
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
				options
				.UseNpgsql(configuration["DefaultConnection"])
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
				.AddScoped<IUserRepository, UserRepository>()
				.AddScoped<IFriendshipRepository, FriendshipRepository>()
				.AddScoped<IUserService, UserService>()
				.AddScoped<IFriendshipService, FriendshipService>()
				.AddSignalR()
					.AddMessagePackProtocol(options =>
					{
						IFormatterResolver resolver = CompositeResolver.Create(
							EnumAsIntegerResolver.Instance,
							StandardResolver.Instance
						);

						options.SerializerOptions = MessagePackSerializerOptions.Standard
							.WithResolver(resolver)
							.WithCompression(MessagePackCompression.Lz4Block)
							.WithSecurity(MessagePackSecurity.UntrustedData);
					})
				.Services;
	}
}
