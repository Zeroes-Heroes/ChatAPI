namespace Services.Recovery;

/// <summary>Base for this module's caller-facing rejections - same convention as <see cref="Services.Sessions.SessionModuleException"/>.</summary>
public abstract class RecoveryModuleException(string message) : Exception(message);

/// <summary>The uploaded envelope is malformed: bad base64url, wrong nonce/tag length, non-canonical or out-of-bounds KDF metadata, or a version that is not the one the server handed out. Never says which - a client has no legitimate use for that distinction, and the recovery secret itself is never involved in this decision.</summary>
public sealed class RecoveryEnvelopeInvalidException(string reason) : RecoveryModuleException($"Recovery envelope is invalid: {reason}");

/// <summary>This account context already has a confirmed recovery password. plan/06 keeps one password per context (a fresh start is how to begin again), so a second setup is refused rather than silently replacing a secret the user still holds.</summary>
public sealed class RecoveryAlreadySetUpException(string reason) : RecoveryModuleException($"Recovery is already set up: {reason}");
