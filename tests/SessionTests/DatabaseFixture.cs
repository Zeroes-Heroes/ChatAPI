using Database.Context;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SessionTests;

/// <summary>
/// Same pattern as ServerDataTests/KeyModuleTests's own DatabaseFixture.cs - a real disposable
/// PostgreSQL the full migration chain is applied to once. Defaults to this task's own dedicated
/// instance/port (55434) and database name so this suite never shares rows with ServerDataTests
/// (55432) or KeyModuleTests (55433).
/// </summary>
public sealed class DatabaseFixture : IDisposable
{
	public string ConnectionString { get; }

	public DatabaseFixture()
	{
		string host = Environment.GetEnvironmentVariable("SESSIONTESTS_PG_HOST") ?? "127.0.0.1";
		string port = Environment.GetEnvironmentVariable("SESSIONTESTS_PG_PORT") ?? "55434";
		string username = Environment.GetEnvironmentVariable("SESSIONTESTS_PG_USER") ?? "postgres";
		string database = Environment.GetEnvironmentVariable("SESSIONTESTS_PG_DB") ?? "chatapp_plan05_sessions_test";
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
	public const string Name = "Session database collection";
}
