using Database.Context;
using Microsoft.EntityFrameworkCore;
using Services.Sync.Contracts;
using Services.Sync.Models;
using Xunit;

namespace MembershipTests;

/// <summary>
/// Review-added regressions for plan/13-history-api.md. They assert the stage's OWN stated invariant -
/// "Почистването на журнала запазва възстановимостта", i.e. a journal read that cannot prove it would
/// skip nothing is refused with CursorInvalid so the device reconciles - on the surface the existing
/// suite does not cover: the Delta-mode CATALOG page, which walks the per-account catalog journal
/// exactly as ReadDeltaAsync does for a chat stream.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SyncHistoryApiReviewRegressionTests(DatabaseFixture fixture)
{
	private static SessionContext SessionOf(TestMember member) =>
		new(new DeviceContext(member.Account.Id, member.Device.Id, member.Instance.Id),
			member.Session.Id,
			member.Device.TrustVersion.ToString());

	[Fact]
	public async Task DeltaModeCatalogPage_BelowThePrunedWatermark_IsRefusedInsteadOfSkipping()
	{
		MembershipTestContext context = new(fixture);
		await using AppDbContext db = context.CreateContext();
		SharedHistoryTests.Group group = await SharedHistoryTests.Group.CreateAsync(context, db);

		// This SQLite copy has already completed its initial restoration, so the next handshake is a
		// Delta one and the catalog page reads the account's catalog JOURNAL rather than the fixed set.
		string instanceId = group.Second.Instance.Id;
		await using (AppDbContext marker = context.CreateContext())
		{
			await marker.SyncLocalInstances
				.Where(i => i.Id == instanceId)
				.ExecuteUpdateAsync(set => set.SetProperty(
					i => i.InitialRestoreCompletedAtUtc, context.Clock.GetUtcNow().UtcDateTime));
		}

		SyncResult<SyncHandshakeResponse> handshake = await context.SyncSessions(db).HandshakeAsync(
			group.Second.Principal, "conn-catalog-prune",
			new SyncHandshakeRequest(SyncProtocol.Version, SessionOf(group.Second)));

		Assert.Null(handshake.Failure);
		Assert.Equal(SyncMode.Delta, handshake.Value!.Mode);

		// The catalog journal below this device's position has been cleaned. This device uploaded no
		// catalog position at all, so its delta would have to start at the very beginning of a journal
		// whose beginning is gone.
		string accountId = group.Second.Account.Id;
		long catalogBound = long.Parse(handshake.Value.CatalogBoundary.Through.Sequence);
		Assert.True(catalogBound > 0);
		await using (AppDbContext maintenance = context.CreateContext())
		{
			await maintenance.SyncAccountCatalogCounters
				.Where(c => c.AccountId == accountId)
				.ExecuteUpdateAsync(set => set.SetProperty(c => c.PrunedThroughCatalogSequence, catalogBound));
		}

		SyncPageRequest request = new(
			SyncProtocol.Version, handshake.Value.SyncSessionId, handshake.Value.CatalogBoundary, null, 50);

		// The /sync/v1/delta surface already refuses this exact read of this exact stream.
		SyncResult<SyncPage> delta = await context.SyncPages(db).ReadDeltaAsync(group.Second.Principal, request);
		Assert.Equal(SyncErrorCode.CursorInvalid, delta.Failure!.Code);

		// /sync/v1/catalog in Delta mode walks the same journal from the same position, so it must give
		// the same answer: a page here would silently omit every pruned catalog entry and still report
		// AppliedThrough at the fixed catalog bound.
		SyncResult<SyncPage> catalog = await context.SyncPages(db).ReadCatalogAsync(group.Second.Principal, request);
		Assert.NotNull(catalog.Failure);
		Assert.Equal(SyncErrorCode.CursorInvalid, catalog.Failure!.Code);
	}
}
