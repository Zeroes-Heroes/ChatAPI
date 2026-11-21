namespace Services.Sync.Contracts;

public sealed record DevicePublicKey(
    string KeyId,
    string Spki);

public sealed record DeviceChallenge(
    string ChallengeId,
    ChallengePurpose Purpose,
    string AccountContextId,
    string DeviceRegistrationId,
    DevicePublicKey PublicKey,
    string Nonce,
    string ExpiresAtUtc);

public sealed record DeviceProof(
    string ChallengeId,
    string Signature);

public sealed record DeviceApproval(
    string ChallengeId,
    SessionContext ApprovingSession,
    string Signature);

public sealed record SessionTokens(
    SessionContext Session,
    string AccessToken,
    string AccessTokenExpiresAtUtc,
    string RefreshToken,
    string RefreshTokenExpiresAtUtc);

public sealed record RefreshSessionRequest(
    string SessionId,
    string RefreshOperationId,
    string RefreshToken,
    DeviceProof Proof);

public sealed record RefreshSessionResult(
    RefreshOutcome Outcome,
    SessionTokens? Tokens,
    int? RetryAfterMs);

/// <summary>Stage 05 addition (permitted per this file's own header: "may be extended before v1 is activated"). Account/device are resolved server-side from SessionId, never supplied by the caller - see Services/Sessions/Service/SessionRefreshCoordinator.cs.</summary>
public sealed record SessionBindingChallengeRequest(
    string SessionId,
    string RefreshOperationId);

/// <summary>Stage 05 addition. The acting account/device/session come from the caller's own authenticated app-token claims, never from this body - only the target is caller-supplied.</summary>
public sealed record RevokeDeviceRequest(string TargetDeviceRegistrationId);

/// <summary>Stage 05. <see cref="LocalInstanceId"/> (stage 06) is bound server-side into the challenge row - the pinned approval array has no field for it - and register rejects any other local instance.</summary>
public sealed record DeviceChallengeRequest(
    string DeviceRegistrationId,
    DevicePublicKey PublicKey,
    string LocalInstanceId);

/// <summary>Stage 05 addition.</summary>
public sealed record DeviceRegisterRequest(
    string LocalInstanceId,
    DeviceProof Proof,
    DeviceApproval? Approval);

public sealed record SessionControlEvent(
    int ProtocolVersion,
    string SessionId,
    SessionControlReason Reason);

public sealed record RecoveryKdf(
    string Algorithm,
    string Salt,
    int MemoryKiB,
    int Iterations,
    int Parallelism);

/// <summary>
/// Stage 06. One stored recovery version as a recovering device sees it. <see cref="VerificationKeySpki"/>
/// is that version's PUBLIC verification key (its private half is sealed inside <see cref="Content"/>), so
/// a trusted device can escrow new author keys to EVERY version the account still accepts for recovery,
/// not only the confirmed one.
/// </summary>
public sealed record RecoveryPackage(
    int ProtocolVersion,
    string AccountContextId,
    string Version,
    RecoveryKdf Kdf,
    EncryptedContent Content,
    string VerificationKeySpki);

/// <summary>
/// Stage 06 upload. Account context comes from the caller's session, never from the body; <see cref="Version"/>
/// must be the server's own NextVersion (it is inside the client AAD, so it can never be renumbered later).
/// <see cref="VerificationKeySpki"/> is the PUBLIC half of the recovery key sealed inside the envelope - the
/// only thing that lets the server later verify proof of secret knowledge without ever holding the secret.
/// </summary>
public sealed record StoreRecoveryEnvelopeRequest(
    int ProtocolVersion,
    string Version,
    RecoveryKdf Kdf,
    EncryptedContent Content,
    string VerificationKeySpki);

/// <summary>
/// Stage 06. <see cref="Candidates"/> is every version stored for this account context, newest first;
/// none is ever retired, because the server cannot know which attempt the system password manager kept.
/// Empty when recovery has never been set up. <see cref="ConfirmedVersion"/> is null until a device
/// confirms the save; once it is set, no further setup is accepted for this context. Each candidate
/// carries its own verification key, which is what new author keys are escrowed to.
/// </summary>
public sealed record RecoveryEnvelopeView(
    string AccountContextId,
    string? ConfirmedVersion,
    string NextVersion,
    IReadOnlyList<RecoveryPackage> Candidates);

/// <summary>Stage 06. Sent only after the system password manager confirmed the save; that closes setup for this account context.</summary>
public sealed record ConfirmRecoveryEnvelopeRequest(string Version);

/// <summary>
/// Stage 06 bootstrap recovery (no session yet). The sealed candidates are useless without the recovery
/// secret; knowledge of it is proven afterwards by <see cref="RecoveryRegisterRequest.RecoveryProof"/>.
/// Every stored version is offered, so an interrupted setup can never lock the user out.
/// </summary>
public sealed record RecoveryBootstrapChallenge(
    DeviceChallenge Challenge,
    IReadOnlyList<RecoveryPackage> Candidates,
    IReadOnlyList<AuthorKeyEscrowEntry> Escrows);

/// <summary>
/// Stage 06. One author key sealed to a recovery version's verification key (ephemeral ECDH P-256 +
/// HKDF-SHA256 + AES-256-GCM, AAD "chat-authorkey-escrow-v1"). The envelope is a one-time snapshot, so
/// this is how authorship created after setup stays recoverable; no secret is needed to add an entry.
/// </summary>
public sealed record AuthorKeyEscrowEntry(
    string RecoveryVersion,
    string AuthorKeyId,
    string ChatId,
    string AuthorKeyVersion,
    string EphemeralPublicKeySpki,
    EncryptedContent Content);

/// <summary>Stage 06. Append-only and idempotent: an author key already escrowed for that version is left exactly as it is.</summary>
public sealed record StoreAuthorKeyEscrowRequest(
    int ProtocolVersion,
    IReadOnlyList<AuthorKeyEscrowEntry> Entries);

/// <summary>Stage 06. <see cref="RecoveryProof"/> is ES256 over the pinned device-proof array with domain "chat-recovery-proof-v1", signed with the recovery key from the opened envelope; the server verifies it against the stored verification key of <see cref="RecoveryVersion"/>.</summary>
public sealed record RecoveryRegisterRequest(
    string LocalInstanceId,
    DeviceProof Proof,
    string RecoveryVersion,
    DeviceProof RecoveryProof);

/// <summary>Stage 06. Author keys sealed to the requesting device's own public key (ephemeral ECDH P-256 + HKDF-SHA256 + AES-256-GCM), so approval alone restores authorship without the recovery secret.</summary>
public sealed record AuthorKeyHandoff(
    string EphemeralPublicKeySpki,
    EncryptedContent Content);

/// <summary>Stage 06. The handoff pending for the calling device. Repeatable: it stays available until the device acknowledges that it imported the keys, so a lost response or a crash cannot destroy the only copy.</summary>
public sealed record AuthorKeyHandoffView(
    string ChallengeId,
    string EphemeralPublicKeySpki,
    EncryptedContent Content);

/// <summary>Stage 06. Sent only after the keys are durably imported locally; idempotent.</summary>
public sealed record AcknowledgeHandoffRequest(string ChallengeId);

/// <summary>Stage 06. Exactly the fields the pinned chat-device-approval-v1 array is signed over, plus the requesting local copy (bound through the challenge row).</summary>
public sealed record PendingApprovalRequest(
    string ChallengeId,
    ChallengePurpose Purpose,
    string AccountContextId,
    string DeviceRegistrationId,
    DevicePublicKey PublicKey,
    string LocalInstanceId,
    string Nonce,
    string RequestedAtUtc,
    string ExpiresAtUtc);

/// <summary>Stage 06. The approving account/device/session come from the caller's own app-token claims, never from this body. <see cref="Handoff"/> is optional only because an approver with no author keys for this account has nothing to hand over.</summary>
public sealed record ApproveDeviceRequest(
    string ChallengeId,
    string Signature,
    AuthorKeyHandoff? Handoff);

/// <summary>Stage 06. <see cref="DeviceApprovalOutcome.AlreadyApproved"/> makes a repeated tap idempotent instead of an error; a refused approval is an HTTP failure, not an outcome value.</summary>
public sealed record ApproveDeviceResult(
    DeviceApprovalOutcome Outcome,
    string ChallengeId,
    string DeviceRegistrationId);

/// <summary>Stage 06. <see cref="OperationId"/> is the device's own durable fresh-start operation id, minted BEFORE the request: it makes a retry resume the successor this operation already created instead of ending it and chaining another.</summary>
public sealed record StartNewAccountContextRequest(string OperationId);

/// <summary>Stage 06. The new, separate account context created for the same verified phone when keys are irrecoverable - never a rebinding of the old one.</summary>
public sealed record NewAccountContextResult(
    string AccountContextId,
    string PreviousAccountContextId);

