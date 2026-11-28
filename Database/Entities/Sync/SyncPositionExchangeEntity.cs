using Database.Enums.Sync;

namespace Database.Entities.Sync;

/// <summary>
/// One batch of one position exchange, kept so an identical retry can be answered with the saved
/// result rather than re-applied ("Identical retries return the saved result; a changed batch with the
/// same exchange/index ... fails InvalidRequest"). <see cref="RequestDigest"/> is what tells the two
/// apart; <see cref="ResultJson"/> is the exact v1 SyncPositionBatchResult that was answered.
///
/// Exchange identity is bound to the sync session, the connection, the rights version and the kind -
/// all stored here - and remains valid only for that sync session, so a reconnect cannot continue an
/// exchange that began on a connection whose readiness has already been invalidated.
/// </summary>
public class SyncPositionExchangeEntity
{
	public SyncPositionExchangeEntity(
		string syncSessionId, string exchangeId, int batchIndex, SyncPositionExchangeKind kind,
		string connectionId, long rightsVersion, string requestDigest, string resultJson, bool final, DateTime createdAtUtc)
	{
		SyncSessionId = syncSessionId;
		ExchangeId = exchangeId;
		BatchIndex = batchIndex;
		Kind = kind;
		ConnectionId = connectionId;
		RightsVersion = rightsVersion;
		RequestDigest = requestDigest;
		ResultJson = resultJson;
		Final = final;
		CreatedAtUtc = createdAtUtc;
	}

	private SyncPositionExchangeEntity() { }

	public string SyncSessionId { get; set; } = null!;

	public string ExchangeId { get; set; } = null!;

	public int BatchIndex { get; set; }

	public SyncPositionExchangeKind Kind { get; set; }

	public string ConnectionId { get; set; } = null!;

	public long RightsVersion { get; set; }

	/// <summary>Canonical unpadded base64url of SHA-256 over the batch's normalized positions.</summary>
	public string RequestDigest { get; set; } = null!;

	public string ResultJson { get; set; } = null!;

	public bool Final { get; set; }

	public DateTime CreatedAtUtc { get; set; }

	public virtual SyncSessionEntity Session { get; set; } = null!;
}
