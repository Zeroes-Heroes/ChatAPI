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

public sealed record RecoveryPackage(
    int ProtocolVersion,
    string AccountContextId,
    string Version,
    RecoveryKdf Kdf,
    EncryptedContent Content);

