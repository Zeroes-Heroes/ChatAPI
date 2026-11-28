namespace Database.Entities.Sync;

/// <summary>
/// Append-only record of one refresh-token rotation attempt, keyed by (SessionId, RefreshOperationId)
/// so the primary key itself is the DB-enforced idempotency boundary: a concurrent duplicate INSERT
/// for the same operation fails on the unique primary key rather than racing to decide "who won".
/// Per docs/sync-contract-v1.md: "Persist RefreshOperationId before submission. Identical retries
/// return the same rotation result within a bounded replay window; reuse under a different operation
/// ID revokes the family."
///
/// <see cref="OldRefreshTokenHash"/>/<see cref="NewRefreshTokenHash"/> keep the full rotation chain
/// queryable for reuse detection (a hash matching any prior row's Old/New - but not the session's
/// CURRENT hash - proves the presented secret was valid at some point but has since been superseded,
/// i.e. reuse). <see cref="ProtectedReplayPayload"/> is the only place the new raw tokens are ever
/// persisted, and only AES-256-GCM-protected (via <see cref="Services.Sessions.Interface.IReplayPayloadProtector"/>,
/// built the same way as <see cref="Services.Keys.Interface.IMessageKeyProtector"/> but over a
/// variable-length JSON payload rather than a fixed-size key - see that interface's own doc comment
/// for why this is a sibling primitive, not a forced reuse of the 32-byte-key-shaped one), purged
/// (set to null) once <see cref="ReplayExpiresAtUtc"/> passes - never written to any log.
/// </summary>
public class SyncRefreshTokenRotationEntity
{
	public SyncRefreshTokenRotationEntity(
		string sessionId, string refreshOperationId, byte[] oldRefreshTokenHash, byte[] newRefreshTokenHash,
		DateTime createdAtUtc, DateTime replayExpiresAtUtc, byte[]? protectedReplayPayload)
	{
		SessionId = sessionId;
		RefreshOperationId = refreshOperationId;
		OldRefreshTokenHash = oldRefreshTokenHash;
		NewRefreshTokenHash = newRefreshTokenHash;
		CreatedAtUtc = createdAtUtc;
		ReplayExpiresAtUtc = replayExpiresAtUtc;
		ProtectedReplayPayload = protectedReplayPayload;
	}

	private SyncRefreshTokenRotationEntity() { }

	public string SessionId { get; set; } = null!;

	public string RefreshOperationId { get; set; } = null!;

	public byte[] OldRefreshTokenHash { get; set; } = null!;

	public byte[] NewRefreshTokenHash { get; set; } = null!;

	public DateTime CreatedAtUtc { get; set; }

	/// <summary>End of the bounded window within which an identical retry of this exact operation replays <see cref="ProtectedReplayPayload"/> instead of being told to reauthenticate.</summary>
	public DateTime ReplayExpiresAtUtc { get; set; }

	/// <summary>AES-256-GCM-protected {accessToken, accessTokenExpiresAtUtc, refreshToken, refreshTokenExpiresAtUtc}, or null once purged after <see cref="ReplayExpiresAtUtc"/>. Never the raw tokens, never logged.</summary>
	public byte[]? ProtectedReplayPayload { get; set; }

	/// <summary>Nonce for <see cref="ProtectedReplayPayload"/>'s AES-GCM protection; null exactly when the payload is null.</summary>
	public byte[]? ProtectedReplayNonce { get; set; }

	/// <summary>Tag for <see cref="ProtectedReplayPayload"/>'s AES-GCM protection; null exactly when the payload is null.</summary>
	public byte[]? ProtectedReplayTag { get; set; }

	/// <summary>Master key version <see cref="ProtectedReplayPayload"/> was protected under; null exactly when the payload is null.</summary>
	public string? ProtectedReplayMasterKeyVersion { get; set; }

	public virtual SyncDeviceSessionEntity Session { get; set; } = null!;
}
