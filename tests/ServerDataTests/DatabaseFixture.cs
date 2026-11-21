using Database.Context;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ServerDataTests;

/// <summary>
/// Applies the real migration chain once to a disposable PostgreSQL database and hands out fresh
/// AppDbContext instances against it. Connection coordinates default to this task's disposable
/// local PostgreSQL instance; override via env vars to point at another disposable instance.
/// Never point this at a shared/production database - tests create and mutate real rows.
/// </summary>
public sealed class DatabaseFixture : IDisposable
{
	public string ConnectionString { get; }

	public DatabaseFixture()
	{
		string host = Environment.GetEnvironmentVariable("SERVERDATATESTS_PG_HOST") ?? "127.0.0.1";
		string port = Environment.GetEnvironmentVariable("SERVERDATATESTS_PG_PORT") ?? "55432";
		string username = Environment.GetEnvironmentVariable("SERVERDATATESTS_PG_USER") ?? "postgres";
		string database = Environment.GetEnvironmentVariable("SERVERDATATESTS_PG_DB") ?? "chatapp_plan02_test";
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
	public const string Name = "ServerData database collection";
}
