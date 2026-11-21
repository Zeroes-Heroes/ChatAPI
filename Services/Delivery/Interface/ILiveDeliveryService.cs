using Services.Delivery.Models;

namespace Services.Delivery.Interface;

/// <summary>
/// Turns one delivery task into the v1 live events it owes (plan/12-outbox-delivery.md: "Live
/// доставката ... носи цялото криптирано съобщение и разрешените ключови данни. Получават го
/// устройствата на получателите и останалите устройства на автора. Преди предоставяне се проверяват
/// текущото доверие и приложимите исторически права").
///
/// It writes nothing. Everything it needs is committed state, and it re-reads all of it at delivery
/// time: the message, its protected key, the author's registered verification key, who currently holds
/// a right to a message created at that instant, and which of those people's device registrations are
/// trusted right now. A duplicate task therefore produces a duplicate live event with the same EventId
/// and no divergent state anywhere.
/// </summary>
public interface ILiveDeliveryService
{
	Task<DeliveryOutcome> DeliverAsync(DeliveryTask task, CancellationToken ct = default);
}

/// <summary>
/// Wakes the publisher loop on the replica that just accepted a message, so live delivery does not wait
/// for the next poll. Latency only: a missed signal (another replica, a crash, a restart) costs at most
/// one <see cref="DeliveryOptions.PollSeconds"/>, because the outbox row itself is what makes the task
/// recoverable.
/// </summary>
public interface IOutboxPublishSignal
{
	void Notify();

	/// <summary>Waits for a signal, returning false when <paramref name="timeout"/> elapsed first.</summary>
	Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct);
}
