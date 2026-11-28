namespace Services.Sessions.Models;

/// <summary>AES-256-GCM output for a rotation's short-lived replay cache - the exact fields <see cref="Database.Entities.Sync.SyncRefreshTokenRotationEntity"/> stores.</summary>
public sealed record ProtectedReplayPayload(string MasterKeyVersion, byte[] Nonce, byte[] Ciphertext, byte[] Tag);
