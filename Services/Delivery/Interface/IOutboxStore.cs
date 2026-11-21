using Services.Delivery.Models;

namespace Services.Delivery.Interface;

/// <summary>
/// The durable queue behind live delivery: <c>SyncOutbox</c>, written inside the acceptance transaction
/// (stage 11) and drained here. It owns every state transition of a row, so the rules below live in one
/// place rather than in the publisher and the consumer separately.
///
/// MESSAGE_SYNC_ANALYSIS.md: "Outbox задачите се вземат атомарно чрез locking/lease. Мрежовото
/// публикуване е извън DB транзакцията." <see cref="ClaimAsync"/> is that atomic claim - a short
/// transaction that takes rows with <c>FOR UPDATE SKIP LOCKED</c> and stamps a lease on them - and it
/// returns before anything is published, so no row lock is ever held across a network call.
/// </summary>
public interface IOutboxStore
{
	/// <summary>
	/// Takes up to <paramref name="max"/> due rows for <paramref name="owner"/> and leases them. Due
	/// means: Pending and not waiting for a backoff, or Leased by an owner whose lease has expired (the
	/// crash-recovery case). Competing replicas skip each other's locked rows instead of blocking, so
	/// two publishers never claim the same row and never wait for each other.
	/// </summary>
	Task<IReadOnlyList<OutboxClaim>> ClaimAsync(string owner, int max, CancellationToken ct = default);

	/// <summary>Records success - only ever called AFTER the broker confirmed the publish and the message really routed.</summary>
	Task MarkPublishedAsync(long id, CancellationToken ct = default);

	/// <summary>
	/// Hands the row back for another attempt with an increasing delay, or - once
	/// <see cref="DeliveryOptions.MaxAttempts"/> is reached - stops retrying it and keeps it for
	/// diagnostics. One statement over the row's own <c>AttemptCount</c>, so two concurrent releases
	/// cannot read-modify-write each other's count away.
	/// </summary>
	Task ReleaseForRetryAsync(long id, string reason, CancellationToken ct = default);

	/// <summary>
	/// Marks a row that can never become deliverable (its message, key or author key is missing). It is
	/// never claimed again, and its reason stays on the row for diagnostics and explicit re-release.
	/// </summary>
	Task MarkUnprocessableAsync(long id, string reason, CancellationToken ct = default);
}
