namespace Services.Hubs.Models;

/// <summary>
/// What a device reports about messages of ONE chat (stage 10, plan/10-recipient-status.md). The
/// account, the device registration and the local instance are NOT part of this payload: the server
/// takes them from the authenticated session, so a device can only ever report about itself.
///
/// Bounded like every other hub payload (docs/sync-contract-v1.md: "Receipt batches cap at both 50 and
/// 32768 bytes"); the count is enforced by
/// <see cref="Services.Statuses.Service.MessageStatusService.MaxReceiptBatch"/>.
/// </summary>
public class MessageReceiptBatch
{
	public int ChatId { get; set; }

	public MessageReceiptItem[] Receipts { get; set; } = [];
}

/// <summary>
/// One message's receipt. <see cref="Kind"/> mirrors the wire contract's ReceiptKind
/// (0 = CiphertextStored, 1 = Received, 2 = Seen) - an unknown value is refused rather than guessed.
/// </summary>
/// <remarks>
/// <see cref="ClientObservedAtUtc"/>/<see cref="WasOffline"/> may accompany a Seen receipt only. The
/// reported instant is stored separately and is deliberately not what fixes the logical Seen moment:
/// accepting a client clock needs the reliable server-authenticated samples the stage-14 handshake
/// carries, which no surface has yet.
/// </remarks>
public class MessageReceiptItem
{
	public int MessageId { get; set; }

	public int Kind { get; set; }

	public DateTime? ClientObservedAtUtc { get; set; }

	public bool WasOffline { get; set; }
}

/// <summary>
/// The answer to one batch - returned from the hub invocation itself, so the reporting device really
/// receives the final result rather than the server computing one and dropping it
/// (plan/10: "Късните неприложими receipts приключват с окончателен резултат").
/// </summary>
public class MessageReceiptBatchResult
{
	public int ChatId { get; set; }

	public MessageReceiptOutcomeItem[] Outcomes { get; set; } = [];
}

/// <summary>
/// What became of ONE reported receipt. <see cref="Disposition"/> mirrors the wire contract's
/// ReceiptDisposition (0 = Applied, 1 = AlreadyApplied, 2 = NotApplicable); NotApplicable is terminal
/// and is the only one whose <see cref="StateVersion"/> is null.
/// </summary>
public class MessageReceiptOutcomeItem
{
	public int MessageId { get; set; }

	public int Disposition { get; set; }

	public long? StateVersion { get; set; }
}
