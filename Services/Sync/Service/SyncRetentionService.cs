using Database.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Services.Sync.Interface;

namespace Services.Sync.Service;

/// <summary>See <see cref="ISyncRetention"/>.</summary>
public sealed class SyncRetentionService(AppDbContext db, TimeProvider timeProvider, ILogger<SyncRetentionService> logger)
	: ISyncRetention
{
	/// <summary>
	/// How far back a journal is kept regardless of anything else. A device offline for longer than this
	/// reconciles its allowed state in full instead of replaying changes - which is the cheaper and the
	/// safer of the two after that long, and is exactly what makes pruning permissible at all.
	/// </summary>
	private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

	/// <summary>One bounded portion per sweep; the next sweep continues with whatever is still over the horizon.</summary>
	private const int MaxStreamsPerSweep = 20;

	public async Task<int> PurgeExpiredSessionsAsync(CancellationToken ct = default)
	{
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;

		// Bounded like every other sweep. The boundary, cursor and exchange rows cascade from the session,
		// so one delete per session is the whole cleanup.
		List<string> expired = await db.SyncSessions.AsNoTracking()
			.Where(s => s.ExpiresAtUtc <= now)
			.OrderBy(s => s.ExpiresAtUtc)
			.Select(s => s.Id)
			.Take(MaxStreamsPerSweep)
			.ToListAsync(ct);

		if (expired.Count == 0)
		{
			return 0;
		}

		return await db.SyncSessions.Where(s => expired.Contains(s.Id)).ExecuteDeleteAsync(ct);
	}

	public async Task<JournalPruneOutcome> PruneJournalsAsync(CancellationToken ct = default)
	{
		DateTime horizon = timeProvider.GetUtcNow().UtcDateTime - Retention;
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;

		List<string> chats = await db.SyncChatChanges.AsNoTracking()
			.Where(c => c.OccurredAtUtc < horizon)
			.Select(c => c.ChatId)
			.Distinct()
			.Take(MaxStreamsPerSweep)
			.ToListAsync(ct);

		List<string> accounts = await db.SyncCatalogEntries.AsNoTracking()
			.Where(e => e.OccurredAtUtc < horizon)
			.Select(e => e.AccountId)
			.Distinct()
			.Take(MaxStreamsPerSweep)
			.ToListAsync(ct);

		int chatRows = 0;
		foreach (string chatId in chats)
		{
			chatRows += await PruneChatAsync(chatId, horizon, now, ct);
		}

		int catalogRows = 0;
		foreach (string accountId in accounts)
		{
			catalogRows += await PruneCatalogAsync(accountId, horizon, now, ct);
		}

		if (chatRows > 0 || catalogRows > 0)
		{
			logger.LogInformation(
				"Pruned {ChatRows} chat change(s) and {CatalogRows} catalog entry(ies) older than {Horizon:o}.",
				chatRows, catalogRows, horizon);
		}

		return new JournalPruneOutcome(chatRows, catalogRows, chats.Count + accounts.Count);
	}

	private async Task<int> PruneChatAsync(string chatId, DateTime horizon, DateTime now, CancellationToken ct)
	{
		await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);

		// The chat's own counter row - the one every allocation and every boundary read goes through -
		// so the watermark and the deletion cannot straddle a concurrent write, and a reader can never
		// observe rows that are gone with a watermark that has not moved yet.
		List<Database.Entities.Sync.SyncChatSequenceCounterEntity> counters = await db.SyncChatSequenceCounters
			.FromSqlInterpolated($"""SELECT * FROM "SyncChatSequenceCounters" WHERE "ChatId" = {chatId} FOR UPDATE""")
			.ToListAsync(ct);

		if (counters.Count != 1)
		{
			await tx.RollbackAsync(ct);
			return 0;
		}

		// The lowest position any LIVE session of this chat might still have to read. A session that has
		// told the server nothing yet (no uploaded position) pins the journal at zero, i.e. nothing is
		// pruned while it is in flight.
		long? floor = await db.SyncSessionBoundaries.AsNoTracking()
			.Where(b => b.ChatId == chatId && b.Session.EndedAtUtc == null && b.Session.ExpiresAtUtc > now)
			.Select(b => (long?)(b.AcknowledgedSequence ?? b.UploadedSequence ?? 0))
			.MinAsync(ct);

		long newest = await db.SyncChatChanges.AsNoTracking()
			.Where(c => c.ChatId == chatId && c.OccurredAtUtc < horizon)
			.Select(c => (long?)c.ChangeSequence)
			.MaxAsync(ct) ?? 0;

		long through = floor is long pinned ? Math.Min(newest, pinned) : newest;
		if (through <= counters[0].PrunedThroughChangeSequence)
		{
			await tx.RollbackAsync(ct);
			return 0;
		}

		int removed = await db.SyncChatChanges
			.Where(c => c.ChatId == chatId && c.ChangeSequence <= through && c.OccurredAtUtc < horizon)
			.ExecuteDeleteAsync(ct);

		await db.SyncChatSequenceCounters
			.Where(c => c.ChatId == chatId)
			.ExecuteUpdateAsync(set => set.SetProperty(c => c.PrunedThroughChangeSequence, through), ct);

		await tx.CommitAsync(ct);
		return removed;
	}

	private async Task<int> PruneCatalogAsync(string accountId, DateTime horizon, DateTime now, CancellationToken ct)
	{
		await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);

		List<Database.Entities.Sync.SyncAccountCatalogCounterEntity> counters = await db.SyncAccountCatalogCounters
			.FromSqlInterpolated($"""SELECT * FROM "SyncAccountCatalogCounters" WHERE "AccountId" = {accountId} FOR UPDATE""")
			.ToListAsync(ct);

		if (counters.Count != 1)
		{
			await tx.RollbackAsync(ct);
			return 0;
		}

		long? floor = await db.SyncSessions.AsNoTracking()
			.Where(s => s.AccountId == accountId && s.EndedAtUtc == null && s.ExpiresAtUtc > now)
			.Select(s => (long?)(s.CatalogAcknowledgedSequence ?? s.CatalogUploadedSequence ?? 0))
			.MinAsync(ct);

		long newest = await db.SyncCatalogEntries.AsNoTracking()
			.Where(e => e.AccountId == accountId && e.OccurredAtUtc < horizon)
			.Select(e => (long?)e.CatalogSequence)
			.MaxAsync(ct) ?? 0;

		long through = floor is long pinned ? Math.Min(newest, pinned) : newest;
		if (through <= counters[0].PrunedThroughCatalogSequence)
		{
			await tx.RollbackAsync(ct);
			return 0;
		}

		int removed = await db.SyncCatalogEntries
			.Where(e => e.AccountId == accountId && e.CatalogSequence <= through && e.OccurredAtUtc < horizon)
			.ExecuteDeleteAsync(ct);

		await db.SyncAccountCatalogCounters
			.Where(c => c.AccountId == accountId)
			.ExecuteUpdateAsync(set => set.SetProperty(c => c.PrunedThroughCatalogSequence, through), ct);

		await tx.CommitAsync(ct);
		return removed;
	}
}
