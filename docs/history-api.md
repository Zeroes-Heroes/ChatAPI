# Server sync and history (stage 13)

plan/13-history-api.md. The server half of a sync: the handshake that fixes what one connection will
download, the HTTP pages that deliver it, the position exchanges that make it ready, and the bounded
cleanup that keeps the journals from growing without ever costing recoverability. The client halves are
stages 14/15; nothing here depends on them.

## Surfaces

| Surface | Where | Answers |
| --- | --- | --- |
| `sync-handshake` | `BaseHub` | `SyncHandshakeResponse`: catalog boundary, up-to-ten priority chat ids, boundary cursor, rights version, Full/Delta |
| `sync-boundary-page` | `BaseHub` | `SyncBoundaryPage` over the fixed scope set, 1..25 per page |
| `sync-exchange-positions` | `BaseHub` | `SyncPositionBatchResult` for Resume / Acknowledge / Check |
| `sync-readiness` | `BaseHub` | `SyncReadiness` (global flags only) |
| `POST /sync/v1/catalog` | `SyncController` | the account's chats, memberships, unread and metadata |
| `POST /sync/v1/history` | `SyncController` | one chat scope's permitted messages with keys and statuses |
| `POST /sync/v1/delta` | `SyncController` | one stream's changes since the position this device uploaded |

Every one of them exchanges the v1 DTOs with `SyncProtocol.CreateJsonOptions()` - the hub as JSON
strings, the controller by reading the raw body and writing the raw answer - so the contract's encoding
and its byte caps hold on the real wire bytes rather than on whatever a transport's default serializer
would produce.

## What the handshake fixes, and why it holds

The handshake runs inside `TrustedMutation` (account → device → session locks) and then takes the
account's **catalog counter row** `FOR UPDATE`. Every membership grant and every freeze for that account
allocates its catalog sequence from exactly that row, so while it is held no rights change of this
account can commit. The captured set, the catalog boundary and the rights version are therefore one
consistent snapshot. The membership module reaches that same row last, after the chat row this
transaction never takes, so the two can never deadlock.

What is written, durably, in that one transaction:

* `SyncSessions` - the session, its connection, its mode, its snapshot id, the catalog boundary and the
  rights version (which **is** the account's catalog position: that number moves exactly when access
  does).
* `SyncSessionBoundaries` - one row per stint the account holds, with that scope's fixed upper bound in
  the chat's change-sequence space. A **frozen** stint's bound is the position it was frozen at; an
  archive cannot grow. `Ordinal` is the enumeration order - priority scopes first - and is what the
  boundary pages and the full catalog page through.
* One `SyncCursors` row: the boundary cursor handed back.

Mode is `Full` until `SyncLocalInstances.InitialRestoreCompletedAtUtc` is set, which survives a restart
and is cleared only by a new local instance (a new SQLite copy).

**Priority**: the up-to-ten set leads with the chats whose newest *incoming logically unread* message is
most recent (`SyncUnreadStates.LatestUnreadCreatedAtUtc`, then the message id), counted only on the
CURRENT active stint, and is filled with the most recently active chats without repetition.

## Full, delta and archives

* **Full** pages read the ENTITY tables: `SyncMessages` inside the stint's `SyncMembershipHistoryGrants`
  ranges, the protected key rows, the recipient/device states, unread, the membership snapshot and the
  membership events derived from the stint rows. They never touch the change journal.
* **Delta** pages walk `SyncChatChanges` / `SyncCatalogEntries` and resolve each pointer now, filtered by
  what the scope may see; a change the caller may not see leaves an empty group so the position still
  advances.
* A **frozen** scope resolves its metadata from `SyncMembershipMetadataSnapshot*` and its statuses from
  `SyncMembershipMessageStatusSnapshots`, at its own snapshot - never at today's state. Stage 08 captured
  that archive in legacy space; stage 13 needed the sequence-space half, so the freeze now writes both
  (`ChatMembershipService.CaptureSequenceSpaceArchiveAsync`).
* A **new grant for old history** moves the account's catalog position, so the fixed view is no longer
  current: the next page answers `RightsChanged`, the next handshake fixes a set that contains the newly
  permitted ranges, and the history surface downloads them from their start - "независимо от текущия
  курсор".

The sequence-space grants are the authority for v1 content. The shared `IChatAccessAuthority` still
decides whether the chat may be read at all (including the stage-06 fresh-start exclusion); the grants
then decide each message. This is the sequence-space authority stage 12 recorded as owed.

## Cursors

Opaque 256-bit random tokens over `SyncCursors` rows: session, account, registration, local instance,
surface, stream, scope, rights version, snapshot, direction, fixed upper bound and next position. They
are **not** authority - every page revalidates the session, the device and the current rights before the
row is even consulted. A cursor row memoizes the exact `(from, through)` it issued and the continuation
it minted, so a retry after a lost response returns the same items and the same next cursor, and the
continuation starts at `through + 1`.

Unknown, expired, foreign, wrong-surface or wrong-bound cursors answer `CursorInvalid`, which is the
client's signal to reconcile its allowed state rather than to restart a stream at zero.

Pages are bounded by 100 items **and** 1 MiB of serialized UTF-8, enforced on the real page bytes; whole
groups are dropped until the page fits, and a single group that cannot fit fails explicitly instead of
producing an unbounded response.

`AppliedThrough` means what it says: a journal page advances position by position, while a content
enumeration (full catalog, history) claims the stream's fixed boundary only on its terminal page - a
half-restored scope must never look like a stream applied up to some middle position.

## Position exchanges and readiness

`ExchangePositions` carries Resume, Acknowledge and Check over one bounded transport; the three share
nothing else. Each batch is stored under `(session, exchange, index)` with a digest of its positions, so
an identical retry returns the saved result while a changed batch under the same index, a gap, a kind
change or anything after `Final` is `InvalidRequest`. The session row is taken `FOR UPDATE` for the
whole batch, so two racing batches of one exchange cannot both decide they are next.

* **Resume** records the uploaded position per scope and, on `Final`, the completed resume set.
* **Acknowledge** verifies the position is within the fixed boundary and monotone, records it, and
  reports the scopes that have reached their bound as `DataReadyScopes`.
* **Check** records nothing and answers the CURRENT committed position, which is how a device learns it
  is behind.

`LiveReady` requires the completed resume set, the catalog acknowledged to its bound and every fixed
scope acknowledged to its own - never one page's doing - and only on the connection the session was
fixed on. Reaching it in a Full session is what sets the local instance's initial-restore marker, after
which every handshake of that SQLite copy is a Delta one.

## Journal cleanup

`ISyncRetention`, run as a third small job on `SessionMaintenanceHostedService`'s loop at its own slower
cadence:

* expired sync sessions are removed, and their boundaries, cursors and exchanges cascade with them -
  "expiration preserves applied positions", it costs a handshake and nothing else;
* journal rows are pruned only below BOTH a 30-day horizon and the lowest position any live session
  still needs, and the stream's pruned watermark is raised in the SAME transaction, under the counter
  row every allocation and every boundary read goes through.

A delta read that would have to start at or below that watermark answers `CursorInvalid`, because the
server cannot prove it would skip nothing. Nothing is lost: the full surfaces read the entity tables and
the archive snapshots, which is exactly why the journal may be cleaned at all.

## Limits of this stage

* Only the SEQUENCE space is served. Legacy `Messages` rows (everything written before stage 11) have no
  v1 identity and are read through the stage-08 legacy history surface instead; a chat with only legacy
  messages answers a v1 catalog entry with no `lastMessageId`.
* No client consumes any of this yet (stages 14/15), and the `SyncReadiness` flags are pulled rather
  than pushed.
* Chat snapshots carry the CALLER's own membership only. Who is in the chat is `memberAccountContextIds`;
  what each of them may read is nobody else's business.
