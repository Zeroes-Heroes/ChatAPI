using System.Security.Claims;
using Services.Sync.Contracts;
using Services.Sync.Models;

namespace Services.Sync.Interface;

/// <summary>
/// The server half of a sync session (plan/13-history-api.md): the handshake that fixes the boundaries
/// and the rights version after live addressing is established, the enumeration of that fixed set, the
/// position exchanges a device resumes/acknowledges/checks with, and the readiness those produce.
///
/// Everything it fixes is DURABLE and shared by replicas (SyncSessions / SyncSessionBoundaries /
/// SyncCursors / SyncPositionExchanges), which is what makes "Sync може да продължи през друга API
/// реплика" true: the handshake arrives on the connection's own replica, every HTTP page may be served
/// by any other.
///
/// It decides no rights of its own: the readable set comes from the membership stints and their
/// sequence-space history grants, and the fresh-start boundary from the same exclusion snapshot every
/// other surface uses.
/// </summary>
public interface ISyncSessionService
{
	/// <summary>
	/// Authenticates the supplied <see cref="SessionContext"/> against the caller's OWN claims, then fixes
	/// - in one transaction, under the account's catalog counter row, so no membership change of this
	/// account can interleave - the catalog boundary, every chat scope's boundary, the up-to-ten priority
	/// set and the rights version. Full mode persists until this account/local instance's initial-restore
	/// marker is set, including across a restart.
	///
	/// A repeated handshake on the SAME connection and rights version reuses the session that is already
	/// fixed, so a lost response costs a round trip and never a second boundary set.
	/// </summary>
	Task<SyncResult<SyncHandshakeResponse>> HandshakeAsync(
		ClaimsPrincipal caller, string connectionId, SyncHandshakeRequest request, CancellationToken ct = default);

	/// <summary>One bounded page of the session's fixed boundary set. A retried cursor answers the same page and the same continuation.</summary>
	Task<SyncResult<SyncBoundaryPage>> GetBoundaryPageAsync(
		ClaimsPrincipal caller, SyncBoundaryPageRequest request, CancellationToken ct = default);

	/// <summary>
	/// One batch of a Resume/Acknowledge/Check exchange. Batches are ordered and idempotent per
	/// (exchange, index); a changed batch under an index that already answered, a gap, or a batch after
	/// Final is InvalidRequest. The completed Resume set and the per-scope acknowledgements recorded here
	/// are the preconditions of readiness - and, for a Full session, of the initial-restore marker.
	/// </summary>
	Task<SyncResult<SyncPositionBatchResult>> ExchangePositionsAsync(
		ClaimsPrincipal caller, string connectionId, SyncPositionBatch batch, CancellationToken ct = default);

	/// <summary>
	/// The session's global readiness flags. Only global flags by contract: per-scope readiness is what
	/// <see cref="SyncPositionBatchResult.DataReadyScopes"/> reports, accumulated by the client for this
	/// connection and rights version.
	/// </summary>
	Task<SyncResult<SyncReadiness>> GetReadinessAsync(
		ClaimsPrincipal caller, string connectionId, string syncSessionId, CancellationToken ct = default);
}
