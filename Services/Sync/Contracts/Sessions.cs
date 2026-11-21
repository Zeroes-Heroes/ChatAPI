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

/// <summary>Stage 05 addition: request body for the two device-enrollment endpoints.</summary>
public sealed record DeviceChallengeRequest(
    string DeviceRegistrationId,
    DevicePublicKey PublicKey);

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

public sealed record RecoveryPackage(
    int ProtocolVersion,
    string AccountContextId,
    string Version,
    RecoveryKdf Kdf,
    EncryptedContent Content);

