namespace Services.Sessions.Models;

/// <summary>Non-ciphertext identifiers a <see cref="Database.Entities.Sync.SyncRefreshTokenRotationEntity.ProtectedReplayPayload"/> row is bound to via AES-GCM associated data - every field that must match exactly for the payload to ever decrypt, so a row copied/misattributed to a different session or operation fails authentication instead of silently decrypting.</summary>
public sealed record ReplayProtectionContext(string AccountId, string SessionId, string RefreshOperationId);
