using Database.Context;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Services.Crypto;
using Services.Sessions.Interface;
using System.Security.Cryptography;

namespace Services.Sessions.Service;

public sealed class SyncAccountProvisioningService : ISyncAccountProvisioningService
{
	private const int AccountIdLength = 20;

	/// <summary>Scopes the undo of one losing insert to that insert alone; re-created per attempt, so a caller provisioning several accounts keeps the ones that already succeeded.</summary>
	private const string ProvisionSavepoint = "sync_account_provision";

	public async Task<SyncAccountEntity> GetOrCreateActiveAccountAsync(AppDbContext db, int legacyUserId, DateTime now, CancellationToken ct = default)
	{
		SyncAccountEntity? existing = await db.SyncAccounts
			.FirstOrDefaultAsync(a => a.LegacyUserId == legacyUserId && a.EndedAtUtc == null, ct);
		if (existing is not null)
		{
			return existing;
		}

		SyncAccountEntity account = new(Base64Url.Encode(RandomNumberGenerator.GetBytes(AccountIdLength)), legacyUserId, now);
		SyncAccountCatalogCounterEntity counter = new(account.Id);
		db.SyncAccounts.Add(account);
		db.SyncAccountCatalogCounters.Add(counter);

		// The caller may own an open transaction (stage 07 provisions an account for every participant of
		// the chat it is creating, inside that creation's own transaction). In PostgreSQL a failed
		// statement aborts the whole transaction, so without a savepoint to roll back to, the recovery
		// below could not run a single query - the "lost the race" path would raise 25P02 instead.
		IDbContextTransaction? ambient = db.Database.CurrentTransaction;
		if (ambient is not null)
		{
			await ambient.CreateSavepointAsync(ProvisionSavepoint, ct);
		}

		try
		{
			await db.SaveChangesAsync(ct);
			return account;
		}
		catch (DbUpdateException)
		{
			// Lost a race to a concurrent first call for the same legacy user - the filtered unique
			// index (IX_SyncAccounts_LegacyUserId_Active) rejected this insert. Detach the losing
			// entities and return the winner's already-committed row instead of surfacing a spurious
			// failure for what is, from the caller's perspective, a successful "get or create". Only
			// this attempt is undone; everything the caller wrote before it stays in its transaction.
			if (ambient is not null)
			{
				await ambient.RollbackToSavepointAsync(ProvisionSavepoint, ct);
			}

			db.Entry(account).State = EntityState.Detached;
			db.Entry(counter).State = EntityState.Detached;
			return await db.SyncAccounts.SingleAsync(a => a.LegacyUserId == legacyUserId && a.EndedAtUtc == null, ct);
		}
	}

	public async Task<SyncAccountEntity> StartNewAccountContextAsync(
		AppDbContext db, int legacyUserId, string freshStartOperationId, DateTime now, CancellationToken ct = default)
	{
		await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);

		string? currentId = await db.SyncAccounts.AsNoTracking()
			.Where(a => a.LegacyUserId == legacyUserId && a.EndedAtUtc == null)
			.Select(a => a.Id)
			.SingleOrDefaultAsync(ct);
		if (currentId is null)
		{
			throw new SessionNotTrustedException("No active account context exists for this identity.");
		}

		// Same account row lock, same order, as every other trust mutation (see
		// DeviceTrustService.RevokeDeviceAsync's own doc comment) - a concurrent enrollment/revocation
		// for the context being ended serializes against this instead of interleaving with it. It is
		// also what serializes two concurrent retries of the SAME operation: the loser only looks for
		// the operation's outcome below after the winner has committed it.
		await db.SyncAccounts
			.FromSqlInterpolated($"""SELECT * FROM "SyncAccounts" WHERE "Id" = {currentId} FOR UPDATE""")
			.AsNoTracking()
			.SingleAsync(ct);

		// Idempotency, scoped to the identity that authenticated this request: a retry after a lost
		// response or a process death resumes the context its own operation already created, and never
		// ends it to chain a second one. An outcome that is no longer the active context (a later fresh
		// start superseded it) is NOT resurrected and not disclosed - the caller is told its operation
		// was superseded and must start a genuinely new one.
		SyncAccountEntity? existingOutcome = await db.SyncAccounts
			.FirstOrDefaultAsync(a => a.LegacyUserId == legacyUserId && a.FreshStartOperationId == freshStartOperationId, ct);
		if (existingOutcome is not null)
		{
			if (existingOutcome.EndedAtUtc is not null)
			{
				throw new SessionNotTrustedException("This fresh-start operation's context was itself superseded.");
			}

			// Nothing was written, so the transaction simply ends (releasing the lock) with the caller
			// holding exactly the successor its own operation created.
			return existingOutcome;
		}

		// Serialize against in-flight legacy writes of the OLD context before ending it: ChatService.
		// CreateChat / BaseHub.SendMessage mutate under TrustedMutation, which holds exactly these device
		// and session rows. Locking them here (account -> device -> session, the module's one order) means
		// either their chat is already committed and the scan below sees it, or they block until this
		// commits and then fail their own commit-time trust check (the account context has ended). Without
		// it, a chat created concurrently would miss the exclusion snapshot and leak into the new context.
		List<string> deviceIds = await db.SyncDeviceRegistrations.AsNoTracking()
			.Where(d => d.AccountId == currentId).Select(d => d.Id).OrderBy(id => id).ToListAsync(ct);
		foreach (string deviceId in deviceIds)
		{
			await db.SyncDeviceRegistrations
				.FromSqlInterpolated($"""SELECT * FROM "SyncDeviceRegistrations" WHERE "AccountId" = {currentId} AND "Id" = {deviceId} FOR UPDATE""")
				.AsNoTracking()
				.ToListAsync(ct);
		}
		List<string> sessionIds = await db.SyncDeviceSessions.AsNoTracking()
			.Where(x => x.AccountId == currentId && x.EndedAtUtc == null).Select(x => x.Id).OrderBy(id => id).ToListAsync(ct);
		foreach (string sessionId in sessionIds)
		{
			await db.SyncDeviceSessions
				.FromSqlInterpolated($"""SELECT * FROM "SyncDeviceSessions" WHERE "AccountId" = {currentId} AND "Id" = {sessionId} FOR UPDATE""")
				.AsNoTracking()
				.ToListAsync(ct);
		}

		// Conditional on still being active: if a concurrent call already ended it, this one must not
		// end a second context or create a duplicate successor.
		int ended = await db.Database.ExecuteSqlInterpolatedAsync(
			$"""UPDATE "SyncAccounts" SET "EndedAtUtc" = {now} WHERE "Id" = {currentId} AND "EndedAtUtc" IS NULL""", ct);
		if (ended != 1)
		{
			throw new SessionNotTrustedException("The account context was concurrently superseded.");
		}

		SyncAccountEntity fresh = new(Base64Url.Encode(RandomNumberGenerator.GetBytes(AccountIdLength)), legacyUserId, now)
		{
			PreviousAccountId = currentId,
			FreshStartOperationId = freshStartOperationId,
		};
		db.SyncAccounts.Add(fresh);
		db.SyncAccountCatalogCounters.Add(new SyncAccountCatalogCounterEntity(fresh.Id));

		// Snapshot the legacy chats that exist for this phone right now: the legacy tables have no
		// account context, so this list is what keeps the new context out of the old history at the
		// real read/publish surfaces (IAccountContextBoundary). Nothing is deleted or reassigned.
		List<int> legacyChatIds = await db.Chats.AsNoTracking()
			.Where(c => c.Users.Any(u => u.Id == legacyUserId))
			.Select(c => c.Id)
			.ToListAsync(ct);
		foreach (int legacyChatId in legacyChatIds)
		{
			db.SyncLegacyContextExclusions.Add(new SyncLegacyContextExclusionEntity(fresh.Id, legacyChatId, now));
		}

		await db.SaveChangesAsync(ct);

		await tx.CommitAsync(ct);
		return fresh;
	}
}
