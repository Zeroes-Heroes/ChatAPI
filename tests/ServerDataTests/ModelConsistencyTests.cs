using Database.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace ServerDataTests;

/// <summary>
/// Equivalent to `dotnet ef migrations has-pending-model-changes`, run as an in-process assertion:
/// the compiled AppDbContext model must exactly match what the last migration's snapshot
/// describes. A mismatch means someone changed an entity/configuration without adding (or without
/// correctly regenerating) the corresponding migration.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ModelConsistencyTests(DatabaseFixture fixture)
{
	[Fact]
	public void CompiledModel_MatchesLastMigrationSnapshot()
	{
		using AppDbContext db = fixture.CreateContext();

		IMigrationsAssembly migrationsAssembly = db.GetService<IMigrationsAssembly>();
		IMigrationsModelDiffer differ = db.GetService<IMigrationsModelDiffer>();
		IModelRuntimeInitializer runtimeInitializer = db.GetService<IModelRuntimeInitializer>();

		// The snapshot's model is conceptual-only; it needs the same runtime finalization the live
		// context's model already went through before GetRelationalModel()/diffing can be used.
		// Both sides must be the design-time model (not the read-optimized db.Model) for the differ.
		IRelationalModel snapshotModel = runtimeInitializer.Initialize(migrationsAssembly.ModelSnapshot!.Model, designTime: true).GetRelationalModel();
		IRelationalModel currentModel = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();

		bool hasPendingChanges = differ.HasDifferences(snapshotModel, currentModel);

		Assert.False(hasPendingChanges, "The AppDbContext model has changes that are not captured by a migration. Run `dotnet ef migrations add` to generate one.");
	}

	[Fact]
	public void SyncTables_ExistAfterMigration()
	{
		using AppDbContext db = fixture.CreateContext();

		string[] expectedTables =
		[
			"SyncAccounts", "SyncDeviceRegistrations", "SyncLocalInstances", "SyncAccountCatalogCounters",
			"SyncChats", "SyncChatSequenceCounters", "SyncMemberships", "SyncMembershipHistoryGrants",
			"SyncMembershipMetadataSnapshots", "SyncMembershipMetadataSnapshotMembers",
			"SyncMembershipMessageStatusSnapshots", "SyncMessages", "SyncMessageKeys",
			"SyncOperationResults", "SyncOutbox", "SyncMessageRecipientStates", "SyncMessageDeviceStates",
			"SyncUnreadStates", "SyncChatChanges", "SyncCatalogEntries"
		];

		IRelationalModel relationalModel = db.Model.GetRelationalModel();
		HashSet<string> actualTables = relationalModel.Tables.Select(t => t.Name).ToHashSet();

		foreach (string table in expectedTables)
			Assert.Contains(table, actualTables);
	}
}
