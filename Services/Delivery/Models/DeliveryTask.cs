using System.Globalization;

namespace Services.Delivery.Models;

/// <summary>
/// What travels through the broker: identifiers only. No message key, no package, no recipient list -
/// docs/sync-contract-v1.md forbids the transient key in "SQLite/outbox/broker/logs", and a task that
/// waited in the queue must never deliver the rights that were true when it was published. The consumer
/// re-reads the committed message and re-evaluates current trust and historical rights itself.
/// </summary>
/// <param name="EventId">
/// Deterministic from the chat change this task is about (<see cref="DeliveryEventId.ForChatChange"/>),
/// so a republished task after a lost confirm and a redelivered one after a missing ack carry the SAME
/// id - which is exactly what the contract's "EventId deduplicates delivery" needs. It is the delivered
/// id for a MESSAGE, whose immutable change position is its version. A STATUS task takes its id from
/// <c>SyncEventId</c> instead, derived from the state's own version, because the same status also
/// reaches a device through an HTTP delta page and the two must deduplicate against each other.
/// </param>
/// <param name="ChangeSequence">The chat stream position of the change, as the wire's decimal string.</param>
/// <remarks>
/// WHAT a task delivers is deliberately not on the wire: the consumer re-reads the outbox row by
/// <paramref name="OutboxId"/> and takes the kind and the subject from there, checking that this task
/// describes that row. Adding a kind here would make the broker payload an authority it must not be, and
/// would need its own compatibility story for messages already sitting in the queue.
/// </remarks>
public sealed record DeliveryTask(
	int ProtocolVersion,
	long OutboxId,
	string ChatId,
	string MessageId,
	string ChangeSequence,
	string EventId);

/// <summary>The one place a delivery event id is formed, so publisher, consumer and tests cannot drift apart.</summary>
public static class DeliveryEventId
{
	public static string ForChatChange(string chatId, long changeSequence) =>
		$"{chatId}:{changeSequence.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// One outbox row claimed under a lease. <see cref="AttemptCount"/> is the value AFTER this claim
/// incremented it, so a publisher can tell a first attempt from a retry without a second read.
/// </summary>
public sealed record OutboxClaim(
	long Id,
	string ChatId,
	string MessageId,
	long ChangeSequence,
	int AttemptCount)
{
	public DeliveryTask ToTask(int protocolVersion) => new(
		protocolVersion,
		Id,
		ChatId,
		MessageId,
		ChangeSequence.ToString(CultureInfo.InvariantCulture),
		DeliveryEventId.ForChatChange(ChatId, ChangeSequence));
}

/// <summary>
/// What one delivery attempt did. <see cref="UnprocessableReason"/> is set only for a record that can
/// never become deliverable (a missing message, key or author key): the task is then finished, the row
/// is kept with its diagnostic reason, and nothing is retried forever
/// ("Изолиран повреден или криптографски невалиден запис се пропуска с устойчива диагностика").
/// </summary>
public sealed record DeliveryOutcome(int DeviceSends, string? UnprocessableReason)
{
	public bool IsUnprocessable => UnprocessableReason is not null;

	public static DeliveryOutcome Delivered(int deviceSends) => new(deviceSends, null);

	public static DeliveryOutcome Unprocessable(string reason) => new(0, reason);
}
