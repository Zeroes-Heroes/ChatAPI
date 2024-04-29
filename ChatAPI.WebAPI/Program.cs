using ChatAPI.Persistence.Database;
using ChatAPI.WebAPI.Extensions;
using ChatAPI.WebAPI.Hubs;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services
	.AddCors(options =>
	{
		options.AddPolicy("AllowOrigin", builder =>
		{
			builder
				.WithOrigins("null")
				.AllowAnyHeader()
				.AllowAnyMethod()
				.AllowCredentials(); // Allow credentials if needed
		});
	})
	.ConfigureAppSettings(builder.Configuration)
	.AddSwagger()
	.AddDatabase(builder.Configuration)
	.AddCaching(builder.Configuration)
	.AddAuthenticationConfigured(builder.Configuration)
	.AddServices()
	.AddEndpointsApiExplorer()
	.AddControllers();

WebApplication app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
	app.UseSwaggerUI();
else
	app.UseExceptionHandler("/error");

app.UseCors("AllowOrigin");

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<ChatHub>("/chatHub");

using (IServiceScope scope = app.Services.CreateScope())
{
	scope.ServiceProvider.GetRequiredService<AppDbContext>().ApplyMigrations();
}

app.Run();
