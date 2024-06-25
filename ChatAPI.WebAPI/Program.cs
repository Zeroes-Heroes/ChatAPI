using ChatAPI.Application.Hubs;
using ChatAPI.Application.Utilities;
using ChatAPI.Persistence.Database;
using ChatAPI.WebAPI.Extensions;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services
	.AddCors(options =>
	{
		options.AddPolicy("AllowOrigin", builder =>
		{
			builder
				//.WithOrigins("*", "null")
				.AllowAnyOrigin()
				.AllowAnyHeader()
				.AllowAnyMethod();
			//.AllowCredentials();
		});
	})
	.ConfigureAppSettings(builder.Configuration)
	.AddSwagger()
	.AddDatabase(builder.Configuration)
	.AddCaching(builder.Configuration)
	.AddAuthenticationConfigured(builder.Configuration)
	.AddAuthorization()
	.InitializeTwilio(builder.Configuration)
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

using (IServiceScope scope = app.Services.CreateScope())
{
	scope.ServiceProvider.GetRequiredService<AppDbContext>().ApplyMigrations();
}

app.Run();
