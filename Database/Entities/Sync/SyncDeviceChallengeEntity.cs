using Database.Enums.Sync;

namespace Database.Entities.Sync;

/// <summary>
/// Persisted, one-use server challenge per docs/sync-contract-v1.md: "Challenges have one-use 256-bit
/// nonces, five-minute expiry and server-side purpose/account/registration/public-key binding." Every
/// field a device proof/approval signature binds (challengeId, purpose, accountContextId,
/// deviceRegistrationId, keyId, spki, nonce, expiresAtUtc) is stored server-side at issuance time, so
/// verification never trusts any of those fields as supplied back by the client - only the signature
/// itself is checked against what this row already says. <see cref="ConsumedAtUtc"/> is set by a
/// single atomic conditional UPDATE ("... WHERE ConsumedAtUtc IS NULL") in
/// <see cref="Services.Sessions.Interface.IDeviceChallengeService"/>, never by a read-then-write, so a
/// concurrent replay of the same challenge can never win a race to be "the" consumption.
/// </summary>
public class SyncDeviceChallengeEntity
{
	public SyncDeviceChallengeEntity(
		string id, ChallengePurpose purpose, string accountId, string deviceRegistrationId,
		string keyId, byte[] publicKeySpki, byte[] nonce, DateTime expiresAtUtc, DateTime createdAtUtc,
		string? sessionId, string? refreshOperationId)
	{
		Id = id;
		Purpose = purpose;
		AccountId = accountId;
		DeviceRegistrationId = deviceRegistrationId;
		KeyId = keyId;
		PublicKeySpki = publicKeySpki;
		Nonce = nonce;
		ExpiresAtUtc = expiresAtUtc;
		CreatedAtUtc = createdAtUtc;
		SessionId = sessionId;
		RefreshOperationId = refreshOperationId;
	}

	private SyncDeviceChallengeEntity() { }

	/// <summary>The wire ChallengeId - a server-generated restricted-ASCII id token, not the nonce itself.</summary>
	public string Id { get; set; } = null!;

	public ChallengePurpose Purpose { get; set; }

	public string AccountId { get; set; } = null!;

	public string DeviceRegistrationId { get; set; } = null!;

	public string KeyId { get; set; } = null!;

	public byte[] PublicKeySpki { get; set; } = null!;

	/// <summary>Random 256-bit (32-byte) one-use value, distinct from <see cref="Id"/>.</summary>
	public byte[] Nonce { get; set; } = null!;

	public DateTime ExpiresAtUtc { get; set; }

	public DateTime CreatedAtUtc { get; set; }

	/// <summary>Null until atomically claimed; see the type's own doc comment.</summary>
	public DateTime? ConsumedAtUtc { get; set; }

	/// <summary>Only set for <see cref="ChallengePurpose.SessionBinding"/> - "Refresh uses SessionBinding challenges additionally bound server-side to SessionId/RefreshOperationId" (docs/sync-contract-v1.md).</summary>
	public string? SessionId { get; set; }

	/// <summary>Only set for <see cref="ChallengePurpose.SessionBinding"/>; see <see cref="SessionId"/>.</summary>
	public string? RefreshOperationId { get; set; }

	public virtual SyncAccountEntity Account { get; set; } = null!;
	public virtual SyncDeviceRegistrationEntity DeviceRegistration { get; set; } = null!;
}
