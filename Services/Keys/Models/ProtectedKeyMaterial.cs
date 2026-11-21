namespace Services.Keys.Models;

/// <summary>AES-256-GCM output of <see cref="Services.Keys.Interface.IMessageKeyProtector"/>: the exact fields docs/sync-contract-v1.md's server protected-key record stores (plus the context it was bound to, for callers that need to persist it alongside).</summary>
public sealed record ProtectedKeyMaterial(string MasterKeyVersion, byte[] Nonce, byte[] Ciphertext, byte[] Tag);
