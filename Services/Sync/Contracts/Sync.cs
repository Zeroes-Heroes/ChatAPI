namespace Services.Sync.Contracts;

public sealed record StreamPosition(
    StreamKind StreamKind,
    string? ChatId,
    string? MembershipId,
    string Sequence);

public sealed record SyncBoundary(
    MembershipScope? Scope,
    StreamPosition Through,
    string SnapshotId);

public sealed record SyncHandshakeRequest(
    int ProtocolVersion,
    SessionContext Session);

public sealed record SyncHandshakeResponse(
    int ProtocolVersion,
    string SyncSessionId,
    string ConnectionId,
    string RightsVersion,
    SyncMode Mode,
    SyncBoundary CatalogBoundary,
    IReadOnlyList<string> PriorityChatIds,
    string? BoundaryCursor,
    string ServerTimeUtc);

public sealed record SyncPageRequest(
    int ProtocolVersion,
    string SyncSessionId,
    SyncBoundary Boundary,
    string? Cursor,
    int Limit);

public sealed record HistoryRange(
    string FromMessageSequence,
    string ThroughMessageSequence,
    string StateSnapshotId,
    string? FrozenAtChangeSequence);

public sealed record MembershipSnapshot(
    MembershipScope Scope,
    string Version,
    MembershipState State,
    IReadOnlyList<HistoryRange> AllowedRanges,
    string MetadataSnapshotId);

public sealed record MembershipEvent(
    MembershipScope Scope,
    MembershipEventKind Kind,
    string SubjectAccountContextId,
    string? ActorAccountContextId,
    string OccurredAtUtc);

public sealed record ChatSnapshot(
    string ChatId,
    string Version,
    ChatKind Kind,
    string Name,
    IReadOnlyList<string> MemberAccountContextIds,
    IReadOnlyList<MembershipSnapshot> Memberships,
    UnreadState Unread,
    string? LastMessageId,
    string? LastMessageCreatedAtUtc);

public sealed record SyncPage(
    int ProtocolVersion,
    string SyncSessionId,
    SyncBoundary Boundary,
    IReadOnlyList<SyncChange> Changes,
    string? NextCursor,
    StreamPosition AppliedThrough,
    bool Complete);

public sealed record SyncChange(
    string EventId,
    StreamPosition Position,
    MembershipScope? Scope,
    ChangeKind Kind,
    ChatSnapshot? Chat,
    MessageDelivery? Message,
    MessageRecipientState? RecipientState,
    MessageDeviceState? DeviceState,
    UnreadState? Unread,
    MembershipSnapshot? Membership,
    MembershipEvent? MembershipEvent);

// Shared bounded exchange for resume, acknowledgement and periodic reconciliation.
public sealed record SyncPositionBatch(
    int ProtocolVersion,
    string SyncSessionId,
    string ConnectionId,
    string RightsVersion,
    string ExchangeId,
    PositionExchangeKind Kind,
    int BatchIndex,
    IReadOnlyList<StreamPosition> Positions,
    bool Final);

public sealed record SyncPositionBatchResult(
    string ExchangeId,
    PositionExchangeKind Kind,
    int BatchIndex,
    bool Complete,
    IReadOnlyList<StreamPosition> ServerPositions,
    IReadOnlyList<MembershipScope> DataReadyScopes);

public sealed record SyncBoundaryPageRequest(
    int ProtocolVersion,
    string SyncSessionId,
    string Cursor,
    int Limit);

public sealed record SyncBoundaryPage(
    string SyncSessionId,
    IReadOnlyList<SyncBoundary> Boundaries,
    string? NextCursor,
    bool Complete);

public sealed record SyncReadiness(
    string SyncSessionId,
    string ConnectionId,
    string RightsVersion,
    bool LiveReady,
    bool InitialRestoreComplete);

public sealed record SyncFailure(
    SyncErrorCode Code,
    int? RetryAfterMs);

public sealed record LiveEvent(
    int ProtocolVersion,
    string AccountContextId,
    string RightsVersion,
    SyncChange Change);

