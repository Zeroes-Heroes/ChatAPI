namespace Services.Sessions;

/// <summary>Base for every exception this module throws for a caller-facing (but non-cryptographic) rejection - distinct from unexpected/programmer-error exceptions, which propagate unchanged.</summary>
public abstract class SessionModuleException(string message) : Exception(message);

/// <summary>A referenced challenge does not exist, is expired, was already consumed, or does not bind the fields the caller expects. Deliberately uninformative about *which* of these is true - see docs/sync-contract-v1.md's replay/forgery invariants - a caller must treat every case identically (fail closed), never branch on "expired vs already used vs forged".</summary>
public sealed class ChallengeInvalidException(string reason) : SessionModuleException($"Device challenge is invalid: {reason}");

/// <summary>A device-proof or device-approval signature failed to verify, or its signed array does not match the server-side-bound challenge fields.</summary>
public sealed class DeviceProofInvalidException(string reason) : SessionModuleException($"Device proof is invalid: {reason}");

/// <summary>The enrollment/approval decision computed at commit no longer matches what the caller's challenge was issued for (e.g. a concurrent revocation introduced the approval barrier between challenge issuance and commit), or an approval was required but not supplied/did not verify.</summary>
public sealed class DeviceApprovalRequiredException(string reason) : SessionModuleException($"Another currently trusted device must approve this registration: {reason}");

/// <summary>The account/device/session this operation targets is not currently trusted (ended session, revoked device, or a trust-version mismatch between the caller's token and the device's current epoch).</summary>
public sealed class SessionNotTrustedException(string reason) : SessionModuleException($"Session is not currently trusted: {reason}");
