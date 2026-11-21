namespace Services.Sessions.Models;

/// <summary>
/// Every field a device-proof/device-approval signature's chat-device-proof-v1 array binds, per
/// docs/sync-contract-v1.md: ["chat-device-proof-v1",challengeId,purpose,accountContextId,
/// deviceRegistrationId,keyId,spki,nonce,expiresAtUtc]. Always built from the server's own persisted
/// <see cref="Database.Entities.Sync.SyncDeviceChallengeEntity"/> row - never from fields the caller
/// supplies in the same request - so a verification call can only ever check a signature against what
/// the server already committed to at challenge-issuance time.
/// </summary>
public sealed record DeviceProofBinding(
	string ChallengeId,
	string Purpose,
	string AccountContextId,
	string DeviceRegistrationId,
	string KeyId,
	byte[] Spki,
	byte[] Nonce,
	string ExpiresAtUtc);
