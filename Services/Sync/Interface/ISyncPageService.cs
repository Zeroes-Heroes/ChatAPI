using System.Security.Claims;
using Services.Sync.Contracts;
using Services.Sync.Models;

namespace Services.Sync.Interface;

/// <summary>
/// The three HTTP page surfaces of a fixed sync session (plan/13-history-api.md: "HTTP предоставя
/// ограничени страници с разрешени чатове, съобщения, ключове, статуси и метаданни"). All three take a
/// <see cref="SyncPageRequest"/> naming the boundary the handshake fixed, and answer a
/// <see cref="SyncPage"/> or a <see cref="SyncFailure"/> - never a successful empty page for a refusal.
///
/// <list type="bullet">
/// <item><see cref="ReadCatalogAsync"/> - the account's chats: the fixed boundary set as chat snapshots
/// while the session is in Full mode, the catalog journal once it is in Delta mode.</item>
/// <item><see cref="ReadHistoryAsync"/> - one chat scope's permitted content, read from the entity
/// tables in message-sequence order, with keys and statuses. Deliberately independent of the change
/// journal, which is what lets the journal be cleaned without costing recoverability - and what makes a
/// newly granted old period downloadable whatever the current cursor says.</item>
/// <item><see cref="ReadDeltaAsync"/> - the changes of one stream since the position this device
/// uploaded, read from the journal.</item>
/// </list>
/// </summary>
public interface ISyncPageService
{
	Task<SyncResult<SyncPage>> ReadCatalogAsync(ClaimsPrincipal caller, SyncPageRequest request, CancellationToken ct = default);

	Task<SyncResult<SyncPage>> ReadHistoryAsync(ClaimsPrincipal caller, SyncPageRequest request, CancellationToken ct = default);

	Task<SyncResult<SyncPage>> ReadDeltaAsync(ClaimsPrincipal caller, SyncPageRequest request, CancellationToken ct = default);
}
