# Shared history and frozen archives, server side (stage 08)

Stage 08 only (plan/08-history-archives.md). Extends the stage-07 membership module with the period an
adder shares, the snapshots a removal/leave freezes, and adds `Services/History/` - the one reader that
resolves "current state vs frozen snapshot" and the one writer of the private per-device record of an
archive having been downloaded.

## Choosing and resolving a shared period

`POST /chat/add-members` now carries `sharedHistory` (`None` | `Last24Hours` | `LastTenDays` |
`CurrentCalendarDay`) and `timeZoneOffsetMinutes`. Only the **choice** travels: the boundary is resolved
on the server, from the server clock, by `Services/Membership/Models/SharedHistorySelection.cs`, so a
client cannot name a start of its own. `CurrentCalendarDay` shifts into the adder's local time, drops the
time of day and shifts back, then clamps the result into `(now - 24h, now]` - a DST transition earlier
the same local day, or an implausible device offset, can therefore never reach further back than the
24-hour option nor into the future.

What is actually granted is the **intersection** of that period with the windows
`IChatAccessAuthority` reports for the adder itself:

- an adder can never share what it cannot read;
- the adder's own inaccessible stretches (the gap of its own rejoin) produce no window, so they stay
  inaccessible for the added member too;
- overlapping source windows are merged, so a stint's stored windows are disjoint.

Each resulting window is stored on `SyncMembershipSharedHistoryWindows` with the option that produced
it and the change sequence of the `Added` event - "Изчислената граница се запазва за всички устройства."
A `None` selection writes no window at all, which is exactly stage 07's behaviour, so a client that does
not send the field changes nothing.

The same add marks the shared older messages logically **Seen** for the added member inside the same
transaction (skipping its own messages and any Seen row an earlier stint already produced), and tells
each author after the commit. They therefore produce no unread and no "new incoming" notification.
Message rows and their keys are never touched.

## What a removal or a leave freezes

`ChatMembershipService.FreezeAsync` already closed the stint's permitted range at the removal. Stage 08
adds, in the same transaction and under the same chat lock:

- `SyncMembershipMetadataSnapshots` (+ `...Members`) - the chat's name/kind and the composition as of the
  exit: everyone still active, plus the departing member recorded as no longer taking part.
- `SyncMembershipLegacyStatusSnapshots` - the public status of every permitted legacy message, for every
  recipient of it, so both the group proportions and the author's individual view stay rebuildable.
  Stage 10 made that literal: a row is written for every person **entitled** to the message at the exit
  (author excluded), with null timestamps where that person had reached no status, so the row count is
  the proportion's denominator and not merely "who happened to have reported something"
  (`docs/recipient-status.md`, "Group proportions").

Both exist because the live rows keep changing for the chat's remaining members: without the copies the
archive would drift, and "Последващи съобщения, статуси, имена и промени в състава остават извън
достъпа му" would be false.

## Reading history: `Services/History/`

`IChatHistoryService.ReadAsync` answers one bounded page of what the caller may read, newest first,
keyset-paged by legacy message id:

- authorization is `IChatAccessAuthority` (which composes the stage-06 fresh-start exclusion); a caller
  with no access gets the same 404 as for a chat that does not exist;
- a message inside a window of an **Active** stint - including a newly shared period that overlaps an
  older archive - is answered with today's statuses; everything held only through an ended stint is
  answered from **that stint's own** snapshot, so a chat left twice answers each stretch from its own
  archive;
- a message in no window is simply absent: no placeholder, no separator;
- a page whose whole scanned stretch was inaccessible comes back empty but still advances
  (`NextBeforeMessageId` is the lowest id scanned), so a restore cannot stall on a gap;
- with `missingOnThisDeviceOnly`, the ids this instance already confirmed are excluded **by the scan**,
  not after it, so each page carries up to `limit` genuinely missing messages. Filtering an
  already-scanned page would make a client with a bounded page budget spend every page on what it
  already has, and an archive longer than that budget would never reach its older messages. The
  pre-filter is applied only while the caller holds no open window at all - then every window is frozen,
  so it selects exactly the rows the per-message rule would have dropped. With an open window (a rejoin
  whose new period overlaps the old archive) the per-message rule stays the only judge and the overlap
  is answered with today's state.

`ConfirmArchiveStoredAsync` is the device's technical confirmation. It writes **only**
`SyncArchiveDeviceDeliveries` (membership, local instance, message): no public status row, no broadcast,
no unread change. It is keyed by `LocalInstanceId`, so a newly authorized device - or the same device
after its local data was cleared - still has to restore the archive even though the person's public
marks say the message was received long ago. `ReadAsync(..., missingOnThisDeviceOnly: true)` skips what
that instance has already confirmed, which is what makes an interrupted restore resume.

The write is one `INSERT ... ON CONFLICT ("MembershipId", "LocalInstanceId", "LegacyMessageId") DO
NOTHING`, not a read-then-insert: two confirmations of the same page genuinely overlap (the archive can
be opened twice in a row, and a retry after a lost response is sent while the first may still be in
flight), and a read-then-insert lets both sides read an "existing" set without the rows and the loser
fail on the primary key. The rows are inserted in ascending message id so two such statements can never
wait on each other. `Recorded` is what this call actually inserted; the rest were already recorded. The
id list is bounded by the same `MaxPageSize` as the read.

The surface is deliberately legacy-shaped (legacy ids, plaintext legacy content). It exists because
messages reach a device only over the live socket today, so without it neither a newly shared period nor
an archive on a new device could ever be delivered. The stage-13 sync protocol replaces it.

## The reconnect sweep no longer touches an archive

`BaseHub.OnConnectedAsync` used to mark every permitted undelivered message `Received`. For a chat the
caller only holds frozen that is a **public** mark on an archive that must stay as it was at the removal,
so the sweep now requires `ChatAccessState.Active`. Archives are restored through `IChatHistoryService`
instead.

The sweep also treats `Seen` as received. The legacy status table is a log with one row per reached
status, and a recipient can reach `Seen` with no `Received` row at all - `SendMessage`'s in-chat fast
path writes only `Seen`, and this stage marks newly shared older history `Seen` at the add. Selecting
candidates by "no `Received` row" alone therefore wrote a `Received` row for messages that were already
`Seen` and published `MessageStatusUpdate(Received)` to their authors, whose view regressed
Seen -> Delivered and never recovered - the opposite of docs/sync-contract-v1.md's "Later device
observations never replace it".

## Migration `20261114120000_AddSharedHistoryAndArchives`

Three additive tables, no existing column changes, no backfill: `SyncMembershipSharedHistoryWindows`,
`SyncMembershipLegacyStatusSnapshots`, `SyncArchiveDeviceDeliveries`. The first two are the
legacy-stream counterparts of `SyncMembershipHistoryGrants` and
`SyncMembershipMessageStatusSnapshots`, which are expressed over `SyncMessages` (stage 11+) and cannot
describe a window or a status over the legacy `Messages` table - `SyncMembershipHistoryGrants` in
particular requires `FromMessageSequence > 0`, and a legacy message has no sequence at all. The same add
still writes a sequence-space grant whenever a shared window actually contains `SyncMessages`, so the two
representations stay consistent the moment stage 11 starts committing them.

`SyncMembershipMetadataSnapshots`/`...Members` already existed (stage 02) and are written for the first
time here.

Like the inherited stage-02 constraint names, several of this migration's explicit FK/CHECK names exceed
PostgreSQL's 63-character identifier limit and are truncated at creation; the truncated prefixes are all
distinct (verified in SQL).

## Concurrency and recovery

- No new lock and no new lock order: every stage-08 write happens inside the existing
  `AddMembersAsync`/`FreezeAsync` bodies, i.e. under the chat counter row this module already holds to
  commit. Verified in SQL: two membership mutations on one chat still queue on that row (~2 s), and the
  archive read + private confirmation never waits for it (~13 ms while it was held), with zero deadlocks.
- The period is resolved from the adder's windows **after** the chat lock is taken, so a concurrent
  removal of the adder either commits first (the add is then refused) or waits.
- The status snapshot is taken in the same transaction as the freeze, so no status committed after the
  freeze leaks in and none committed before it is missed.
- A retried add is still refused for an already-active member, so a period cannot be widened or the Seen
  marks duplicated; a retried confirmation writes nothing new.
- A crash between commit and broadcast loses only live events: `GET /chat/get-chats` plus
  `GET /chat/history` re-derive everything from committed rows.

## Later-stage obligations

1. **Server unread and sync priority (stages 10/13/15)** — the unread half is **done** in stage 10:
   `SyncUnreadStates` (plus its legacy message pointers) is now written and read per membership stint,
   so a rejoin answers its new stint's counter and never the frozen archive's. The sync priority set
   remains stages 13/15. The Seen marking this stage performs at an add now goes through
   `Services/Statuses` with reason `HistoryShared` - see `docs/recipient-status.md`.
2. **Key grants (stages 11-13).** `IChatHistoryService` is the gate a message-key grant must pass; the
   legacy history it serves today is plaintext legacy content, not key-protected packages.
3. **`SyncMembershipMessageStatusSnapshots`** (sequence space) stays unwritten until `SyncMessages`
   exist; stage 11 must start writing it alongside the legacy snapshot, or replace it.
4. **Profile/unknown-identity filtering (stage 09)** — done: `Services/Contacts`'s `IProfileVisibility`
   is the one resolver, and `GET /chat/history` now also carries stage 09's `canAuthorRevise`. The
   history page and the frozen metadata snapshot still name participants by numeric id only, so any
   later surface that renders those ids must route them through that resolver
   (`docs/contacts-privacy.md`).
