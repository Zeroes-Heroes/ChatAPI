namespace Services.Sync.Contracts;

public enum OperationKind { SendText }
public enum ReceiptKind { CiphertextStored, Received, Seen }
public enum ReceiptDisposition { Applied, AlreadyApplied, NotApplicable }
public enum SeenReason { Observed, HistoryShared, OwnSend }
public enum StreamKind { Catalog, Chat }
public enum SyncMode { Full, Delta }
public enum MembershipState { Active, Frozen }
public enum ChatKind { Direct, Group }
public enum ChangeKind { Chat, Message, RecipientState, DeviceState, Unread, Membership, MembershipEvent }
public enum SyncErrorCode { CursorInvalid, RightsChanged, SessionExpired, DeviceRevoked, Unavailable, UnsupportedVersion, InvalidRequest }
public enum ChallengePurpose { Enrollment, Recovery, SessionBinding, DeviceApproval }
public enum RefreshOutcome { Rotated, Retryable, ReauthenticationRequired, DeviceRevoked, ReplayDetected }
public enum SessionControlReason { Expired, Revoked, Draining, DependencyUnavailable }
public enum OperationErrorCode { Unavailable, RateLimited, SessionExpired, DeviceRevoked, MembershipChanged, Forbidden, InvalidPackage, OperationConflict, UnsupportedVersion, PayloadTooLarge }

/// <summary>Stage 06: result of recording an approval grant for one exact pending registration. Only success shapes - every refusal (unknown/expired request, untrusted approver) is an HTTP failure, never a value here.</summary>
public enum DeviceApprovalOutcome { Recorded, AlreadyApproved }

public enum PositionExchangeKind { Resume, Acknowledge, Check }
public enum MembershipEventKind { Added, Left, Removed }
