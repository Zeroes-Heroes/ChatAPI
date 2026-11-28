using Database.Context;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KeyModuleTests;

/// <summary>
/// Same pattern as `ServerDataTests/DatabaseFixture.cs` (a separate copy, not a shared reference,
/// so this stage leaves that inherited test project untouched) - a real disposable PostgreSQL
/// database that the full migration chain is applied to once. Defaults to this task's own disposable
/// instance on a different database name than ServerDataTests, so the two suites never share rows.
/// </summary>
public sealed class DatabaseFixture : IDisposable
{
	public string ConnectionString { get; }

	public DatabaseFixture()
	{
		string host = Environment.GetEnvironmentVariable("KEYMODULETESTS_PG_HOST") ?? "127.0.0.1";
		string port = Environment.GetEnvironmentVariable("KEYMODULETESTS_PG_PORT") ?? "55433";
		string username = Environment.GetEnvironmentVariable("KEYMODULETESTS_PG_USER") ?? "postgres";
		string database = Environment.GetEnvironmentVariable("KEYMODULETESTS_PG_DB") ?? "chatapp_plan04_keymodule_test";
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
	public const string Name = "KeyModule database collection";
}
