using Database.Context;
using Database.Entities.Sync;
using Microsoft.EntityFrameworkCore;
using Services.Crypto;
using Services.Sessions.Interface;
using System.Security.Cryptography;

namespace Services.Sessions.Service;

public sealed class SyncAccountProvisioningService : ISyncAccountProvisioningService
{
	private const int AccountIdLength = 20;

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
			// failure for what is, from the caller's perspective, a successful "get or create".
			db.Entry(account).State = EntityState.Detached;
			db.Entry(counter).State = EntityState.Detached;
			return await db.SyncAccounts.SingleAsync(a => a.LegacyUserId == legacyUserId && a.EndedAtUtc == null, ct);
		}
	}
}
