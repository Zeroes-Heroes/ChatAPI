using Microsoft.Extensions.FileProviders;

namespace WebAPI.Extensions
{
	public static class WebApplicationExtensions
	{
		public static WebApplication UseSwaggerUI(this WebApplication app)
		{
			app.UseSwagger().UseSwaggerUI(c =>
			{
				c.SwaggerEndpoint("/swagger/v1/swagger.json", "API v1");
				c.RoutePrefix = "swagger";
				c.InjectStylesheet("/swagger-ui/custom.css");
			});

			app.UseStaticFiles(new StaticFileOptions
			{
				FileProvider = new PhysicalFileProvider(
					Path.Combine(Directory.GetCurrentDirectory(), "wwwroot/swagger-ui")),
				RequestPath = "/swagger-ui"
			});

			return app;
		}
	}
}
