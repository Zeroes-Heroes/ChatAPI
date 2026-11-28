using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Database.Context;

/// <summary>
/// Design-time only factory used by `dotnet ef migrations add/script`. The connection string here
/// is never actually connected to for migrations add/script (EF only needs the provider and model),
/// and this factory is not used at application runtime - see Services.Extensions.ServiceCollectionExtension.AddDatabase.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
	public AppDbContext CreateDbContext(string[] args)
	{
		DbContextOptionsBuilder<AppDbContext> optionsBuilder = new();
		optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Username=design_time;Password=design_time;Database=design_time");
		return new AppDbContext(optionsBuilder.Options);
	}
}
