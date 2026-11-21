using Database.Context;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MembershipTests;

/// <summary>
/// Same pattern as the stage 02/04/05/06 suites: a real disposable PostgreSQL the full migration chain is
/// applied to once, on this stage's own port/database so no suite shares rows with another.
/// </summary>
public sealed class DatabaseFixture : IDisposable
{
	public string ConnectionString { get; }

	public DatabaseFixture()
	{
		string host = Environment.GetEnvironmentVariable("MEMBERSHIPTESTS_PG_HOST") ?? "127.0.0.1";
		string port = Environment.GetEnvironmentVariable("MEMBERSHIPTESTS_PG_PORT") ?? "55437";
		string username = Environment.GetEnvironmentVariable("MEMBERSHIPTESTS_PG_USER") ?? "postgres";
		string database = Environment.GetEnvironmentVariable("MEMBERSHIPTESTS_PG_DB") ?? "chatapp_plan07_membership_test";
		ConnectionString = $"Host={host};Port={port};Username={username};Database={database}";

		using AppDbContext migrateContext = CreateContext();
		migrateContext.Database.Migrate();
	}

	public AppDbContext CreateContext()
	{
		DbContextOptionsBuilder<AppDbContext> optionsBuilder = new();
		optionsBuilder.UseNpgsql(ConnectionString);
		return new AppDbContext(optionsBuilder.Options);
	}

	public void Dispose()
	{
	}
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
	public const string Name = "Membership database collection";
}
