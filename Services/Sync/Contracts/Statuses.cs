namespace Services.Sync.Contracts;

public sealed record ClockSample(
    string SampleId,
    string ServerReceivedAtUtc,
    string ServerSentAtUtc,
    string ClientSentAtUtc,
    string ClientReceivedAtUtc);

public sealed record SeenObservation(
    string ClientObservedAtUtc,
    bool WasOffline,
    ClockSample? BeforeDisconnect,
    ClockSample? AfterReconnect);

public sealed record ReceiptRequest(
    int ProtocolVersion,
    OperationIdentity Operation,
    MembershipScope Scope,
    string MessageId,
    ReceiptKind Kind,
    SeenObservation? Observation);

public sealed record ReceiptResult(
    OperationIdentity Operation,
    string MessageId,
    ReceiptDisposition Disposition,
    string? StateVersion,
    string ServerReceivedAtUtc);

public sealed record MessageDeviceState(
    string MessageId,
    DeviceContext Device,
    string Version,
    string? CiphertextStoredAtUtc,
    string? ReceivedAtUtc,
    string? ObservedSeenAtUtc);

public sealed record RecipientSeen(
    SeenReason Reason,
    string SeenAtUtc);

public sealed record MessageRecipientState(
    string MessageId,
    string RecipientAccountContextId,
    string Version,
    string? ReceivedAtUtc,
    RecipientSeen? Seen);

public sealed record UnreadState(
    string ChatId,
    string AccountContextId,
    string MembershipId,
    string Version,
    string Count,
    string? OldestUnreadMessageId,
    string? LatestUnreadMessageId,
    string? LatestUnreadCreatedAtUtc);

