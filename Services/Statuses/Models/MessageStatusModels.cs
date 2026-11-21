using Services.Sync.Contracts;

namespace Services.Statuses.Models;

/// <summary>
/// One device's report about one message. The wire shape (docs/sync-contract-v1.md's ReceiptRequest)
/// carries an operation identity and a membership scope as well; both are derived here from the
/// authenticated session instead of being trusted from the body - "Server derives the device/account
/// from authenticated session and checks supplied context" - so this is deliberately the smaller,
/// legacy-shaped submission a device can make today.
/// </summary>
/// <param name="Kind">
/// <see cref="ReceiptKind.CiphertextStored"/> is private device progress and never produces a public
/// mark; <see cref="ReceiptKind.Received"/> asserts a completed durable write (and, from stage 11, an
/// available key plus a successful verify/decrypt); <see cref="ReceiptKind.Seen"/> asserts a real
/// observation on this device.
/// </param>
/// <param name="Observation">Only <see cref="ReceiptKind.Seen"/> may carry one; every other kind requires null.</param>
public sealed record MessageReceipt(int LegacyMessageId, ReceiptKind Kind, ReportedObservation? Observation = null);

/// <summary>
/// What a device reports about WHEN it observed a message. The instant is recorded as the client's own
/// reported time ("Оригиналният клиентски час се пази отделно") and is deliberately NOT what fixes the
/// logical Seen moment: the pinned contract accepts a client time only on the strength of a
/// "reliable server-authenticated sample" before the disconnect and after the reconnect, and no such
/// sample exists on any surface yet (the sync handshake that carries them is stage 14). Until then the
/// server's own receipt time is the authoritative one, which is exactly the contract's
/// "Missing/unreliable/out-of-range samples ... use server receipt time" branch - never a client-echoed
/// timestamp taken on trust.
/// </summary>
public sealed record ReportedObservation(DateTime ClientObservedAtUtc, bool WasOffline);

/// <summary>The outcome of ONE receipt. <paramref name="StateVersion"/> is null exactly for <see cref="ReceiptDisposition.NotApplicable"/>, per the pinned contract.</summary>
public sealed record MessageReceiptOutcome(int LegacyMessageId, ReceiptDisposition Disposition, long? StateVersion);

/// <summary>What one batch of receipts did, plus the public marks to publish AFTER the commit.</summary>
public sealed record ReceiptBatchResult(
	int ChatId,
	IReadOnlyList<MessageReceiptOutcome> Outcomes,
	IReadOnlyList<RecipientStatusMark> Marks);

/// <summary>
/// One recipient's committed public transition, grouped by the author it has to be reported to
/// ("Статусът се изпраща на автора"). Carried out of the write so the broadcast can run strictly after
/// the commit - a crash before it loses a live event, never a status.
/// </summary>
public sealed record RecipientStatusMark(
	int AuthorLegacyUserId,
	int RecipientLegacyUserId,
	bool IsSeen,
	DateTime AtUtc,
	IReadOnlyList<int> LegacyMessageIds);

/// <summary>
/// What a just-committed own message did to the chat's statuses (stage 10's half of the atomic
/// acceptance stage 11 completes).
/// </summary>
/// <param name="RecipientCount">
/// How many PEOPLE may receive this message - the denominator of its group proportions, with the
/// author excluded ("Получателите се броят по хора, като подателят е изключен").
/// </param>
/// <param name="OwnSendSeen">
/// The older incoming messages this send marked logically Seen for its own sender
/// ("При първия успешен commit на собствено ново съобщение по-старите входящи в текущия разрешен обхват
/// на чата се маркират логически прочетени за подателя"), to be reported to their authors after commit.
/// </param>
/// <param name="UnreadMembershipIds">
/// Stage 11: the stints whose unread counter this send actually changed, so the caller can journal one
/// <c>SyncChangeKind.Unread</c> entry per changed row from the counter it already holds. Empty for the
/// legacy path, which writes no journal entry for a legacy status change (the live event is its
/// delivery).
/// </param>
/// <param name="OwnSendSyncMessageIds">
/// Stage 11: the v1 messages this send marked Seen/OwnSend for its own sender, for the matching
/// <c>SyncChangeKind.RecipientState</c> journal entries.
/// </param>
public sealed record NewMessageStatusEffects(
	int RecipientCount,
	IReadOnlyList<RecipientStatusMark> OwnSendSeen,
	IReadOnlyList<string> UnreadMembershipIds,
	IReadOnlyList<string> OwnSendSyncMessageIds)
{
	public NewMessageStatusEffects(int recipientCount, IReadOnlyList<RecipientStatusMark> ownSendSeen)
		: this(recipientCount, ownSendSeen, [], []) { }

	public static readonly NewMessageStatusEffects None = new(0, []);
}

/// <summary>One person's logical status for one message, as it is shown to the author and counted in the proportions.</summary>
public sealed record RecipientStatus(
	int RecipientLegacyUserId,
	DateTime? ReceivedAtUtc,
	DateTime? SeenAtUtc,
	SeenReason? SeenReason);

/// <summary>
/// The status view of ONE message: who has the right to it (author excluded) and what each of those
/// people's logical state is. The proportions are <c>Received</c>/<c>Seen</c> counts over
/// <paramref name="RecipientCount"/>; a single remaining recipient is still just "stored" until it
/// proves otherwise, which is what "При единствен останал подател се показва отметката за записано"
/// reduces to.
/// </summary>
public sealed record MessageStatusView(
	int LegacyMessageId,
	int RecipientCount,
	IReadOnlyList<RecipientStatus> Recipients)
{
	public int ReceivedCount => Recipients.Count(r => r.ReceivedAtUtc is not null || r.SeenAtUtc is not null);

	public int SeenCount => Recipients.Count(r => r.SeenAtUtc is not null);
}
