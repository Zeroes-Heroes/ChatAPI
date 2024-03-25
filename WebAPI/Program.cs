using WebAPI.Extensions;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services
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

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
