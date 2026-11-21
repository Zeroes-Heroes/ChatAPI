using Database.Context;
using Database.Entities.Sync;
using Database.Enums.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Services.Delivery.Interface;
using Services.Delivery.Models;

namespace Services.Delivery.Service;

/// <summary>See <see cref="IOutboxStore"/>.</summary>
public sealed class OutboxStore(AppDbContext db, IOptions<DeliveryOptions> options, TimeProvider timeProvider) : IOutboxStore
{
	private const int Pending = (int)SyncOutboxStatus.Pending;
	private const int Leased = (int)SyncOutboxStatus.Leased;
	private const int Published = (int)SyncOutboxStatus.Published;
	private const int Failed = (int)SyncOutboxStatus.Failed;

	public async Task<IReadOnlyList<OutboxClaim>> ClaimAsync(string owner, int max, CancellationToken ct = default)
	{
		if (max <= 0)
		{
			return [];
		}

		DeliveryOptions config = options.Value;
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;
		DateTime leasedUntil = now.AddSeconds(config.LeaseSeconds);

		// Its own short transaction: the row locks taken below must survive until the lease is stamped,
		// and must be released before anything is published (the publish is a network call and may take
		// as long as the broker takes).
		await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);

		// SKIP LOCKED is what makes two replicas polling at the same instant take DISJOINT rows without
		// either waiting: a row another publisher is claiming right now is simply not a candidate here.
		// An expired lease is a candidate again, which is how a publisher that died between claiming and
		// publishing is recovered - at the cost of a possible duplicate publish, which the task's stable
		// EventId makes harmless.
		List<SyncOutboxEntity> rows = await db.SyncOutbox.FromSqlInterpolated($"""
			SELECT * FROM "SyncOutbox"
			WHERE ("Status" = {Pending} AND ("NextAttemptAtUtc" IS NULL OR "NextAttemptAtUtc" <= {now}))
			   OR ("Status" = {Leased} AND "LeasedUntilUtc" IS NOT NULL AND "LeasedUntilUtc" <= {now})
			ORDER BY "Id"
			LIMIT {max}
			FOR UPDATE SKIP LOCKED
			""").ToListAsync(ct);

		foreach (SyncOutboxEntity row in rows)
		{
			row.Status = SyncOutboxStatus.Leased;
			row.LeaseOwner = owner;
			row.LeasedUntilUtc = leasedUntil;
			row.NextAttemptAtUtc = null;
			// Counted at claim time, not at failure time, so a publisher that crashes without reporting
			// anything still consumes an attempt and a permanently poisonous row cannot be retried forever.
			row.AttemptCount += 1;
		}

		await db.SaveChangesAsync(ct);
		await transaction.CommitAsync(ct);

		return [.. rows.Select(r => new OutboxClaim(r.Id, r.ChatId, r.MessageId, r.ChangeSequence, r.AttemptCount))];
	}

	public Task MarkPublishedAsync(long id, CancellationToken ct = default)
	{
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;

		return db.Database.ExecuteSqlInterpolatedAsync($"""
			UPDATE "SyncOutbox"
			SET "Status" = {Published}, "PublishedAtUtc" = {now}, "LeaseOwner" = NULL, "LeasedUntilUtc" = NULL,
				"NextAttemptAtUtc" = NULL, "LastError" = NULL
			WHERE "Id" = {id}
			""", ct);
	}

	public Task ReleaseForRetryAsync(long id, string reason, CancellationToken ct = default)
	{
		DeliveryOptions config = options.Value;
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;

		// One statement over the row's OWN AttemptCount: the backoff and the "stop retrying" decision are
		// computed from the value the database holds, so a publisher and a consumer releasing the same row
		// concurrently cannot overwrite each other's count or resurrect an exhausted row.
		return db.Database.ExecuteSqlInterpolatedAsync($"""
			UPDATE "SyncOutbox"
			SET "Status" = CASE WHEN "AttemptCount" >= {config.MaxAttempts} THEN {Failed} ELSE {Pending} END,
				"NextAttemptAtUtc" = CASE
					WHEN "AttemptCount" >= {config.MaxAttempts} THEN NULL
					ELSE {now} + make_interval(secs => LEAST(
						{(double)config.RetryMaxSeconds},
						{(double)config.RetryBaseSeconds} * power(2, GREATEST("AttemptCount" - 1, 0))))
				END,
				"LeaseOwner" = NULL, "LeasedUntilUtc" = NULL, "LastError" = {Truncate(reason)}
			WHERE "Id" = {id}
			""", ct);
	}

	public Task MarkUnprocessableAsync(long id, string reason, CancellationToken ct = default) =>
		db.Database.ExecuteSqlInterpolatedAsync($"""
			UPDATE "SyncOutbox"
			SET "Status" = {Failed}, "NextAttemptAtUtc" = NULL, "LeaseOwner" = NULL, "LeasedUntilUtc" = NULL,
				"LastError" = {Truncate(reason)}
			WHERE "Id" = {id}
			""", ct);

	/// <summary>Diagnostics, not a payload: a broker/driver message is bounded before it reaches a column with no length limit.</summary>
	private static string Truncate(string reason) =>
		reason.Length <= 500 ? reason : reason[..500];
}
