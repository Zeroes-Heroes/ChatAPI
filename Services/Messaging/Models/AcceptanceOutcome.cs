using Services.Statuses.Models;
using Services.Sync.Contracts;

namespace Services.Messaging.Models;

/// <summary>
/// What the caller of <see cref="Interface.IMessageAcceptanceService"/> must do after the service
/// returns: answer <paramref name="Result"/> (the server ACK, sent strictly AFTER the commit), publish
/// <paramref name="OwnSendSeen"/>, and - when <paramref name="SessionUntrusted"/> - abort the
/// connection, exactly as every other mutating hub method does.
/// </summary>
/// <param name="OwnSendSeen">
/// The legacy-space status marks this send committed for its own sender, to be published to their
/// authors. Empty for everything except a fresh acceptance; the v1-space marks travel through the chat
/// change journal instead and are delivered by the outbox stage.
/// </param>
/// <param name="OutboxId">
/// The row this acceptance queued for post-commit publish, or null when nothing new was committed (a
/// retry, or a refusal). Stage 12 owns claiming and publishing it; the server ACK never waits for it.
/// </param>
public sealed record AcceptanceOutcome(
	OperationResult Result,
	IReadOnlyList<RecipientStatusMark> OwnSendSeen,
	long? OutboxId,
	bool SessionUntrusted)
{
	public static AcceptanceOutcome Of(OperationResult result) => new(result, [], null, SessionUntrusted: false);

	public static AcceptanceOutcome Untrusted(OperationResult result) => new(result, [], null, SessionUntrusted: true);
}
