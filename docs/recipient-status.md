# Recipient statuses and unread, server side (stage 10)

Stage 10 only (plan/10-recipient-status.md). Adds `Services/Statuses/` — the one owner of what a
message's status means — and makes every reachable surface go through it. Everything is expressed over
the legacy `Messages` table, because that is still where every real message lives; `SyncMessages` and
the sequence-space state tables start in stage 11.

## Three facts that used to be one

| Fact | Row | Who writes it |
| --- | --- | --- |
| this SQLite copy stored the package | `SyncLegacyMessageDeviceStates.CiphertextStoredAtUtc` | the device's own receipt |
| this SQLite copy holds a readable copy | `…DeviceStates.ReceivedAtUtc` | the device's own receipt |
| this SQLite copy really observed it | `…DeviceStates.ObservedSeenAtUtc` | the device's own receipt / entering the chat |
| the PERSON received it | `SyncLegacyMessageRecipientStates.ReceivedAtUtc` | the first device that proved it |
| the PERSON saw it, and why | `…RecipientStates.SeenAtUtc` + `SeenReason` | the first accepted transition |
| how much is unread for one stint | `SyncUnreadStates.Count` (+ the stage-10 legacy pointers) | the status service |
| the server stored the message | the `new-message-created` ACK | `send-message`, unchanged |

Stored ciphertext never produces a public mark ("Записването само на ciphertext се отчита отделно").
`Received` requires a completed durable write — and, from stage 11, an available key and a successful
verify/decrypt, which is why the receipt is a device assertion rather than something the server infers.
The logical rows are keyed by **MembershipId**, so a rejoin starts a clean logical state while the old
stint's rows stand as the archive's own value.

## What a device reports

`BaseHub.message-receipt` (`MessageReceiptBatch`) carries only a chat id and up to 50 (message, kind)
pairs; the account, the device registration and the local instance come from the authenticated session,
so a device can only ever report about itself. `IMessageStatusService.AcceptReceiptsAsync` answers one
`ReceiptDisposition` per message:

- **Applied / AlreadyApplied** — with the row's `Version` as `StateVersion`. A retry after a lost
  response is `AlreadyApplied` with the same version: no second unread decrement, no moved Seen time,
  no duplicate legacy status row and nothing published twice.
- **NotApplicable** (`StateVersion` null, terminal) — an unknown message, a message of another chat, or
  one this caller holds no window for. The device drops it instead of retrying forever.
- A message held only through a **frozen** window updates the private device row and nothing else
  ("Публичните отметки и unread в замразения чат остават непроменени").
- A receipt about the caller's **own** message updates its device row only: an author is not its own
  recipient and is excluded from every proportion.

An unknown `Kind` is skipped rather than guessed, and only a `Seen` receipt may carry an observation.

The hub method **returns** those outcomes to the device that sent the batch (a refused batch answers
null), so a stale receipt really terminates for its sender instead of the server computing a final
answer and dropping it. The client-side queue that will act on it — hold a receipt until it is
`Applied`, drop it on `NotApplicable` — is **stage 16**; today's client is fire-and-forget and simply
re-reports, which the server answers `AlreadyApplied` for.

A chat with **no membership projection** (created before stage 07) has no stint to key a logical row
by, so there the legacy `MessagesStatus` log is both the projection and the whole state. It is applied
with the same monotonicity: a `Received` arriving after a `Seen` is `AlreadyApplied` and publishes
nothing (a `Seen` already implies the person has the message, and republishing `Received` would take
the author's tick from Seen back to Delivered with nothing to restore it). Its `StateVersion` is the
number of transitions the log records for that pair — nothing → received → seen — which is monotonic
because the log is append-only and this path never appends a status below the one already recorded.

## Where a logical Seen comes from

| Reason | Trigger | Unread |
| --- | --- | --- |
| `Observed` | a Seen receipt, or `chat-entered` | decremented |
| `HistoryShared` | `POST /chat/add-members` sharing older history | untouched (starts at zero) |
| `OwnSend` | the first commit of the sender's own new message, for the older incoming messages inside its **current** windows | decremented |

The first accepted transition fixes reason **and** time; a later observation on another device changes
neither ("Следващите device Seen записи запазват този общ час"). Seen also backfills `ReceivedAtUtc`, so
an author's view can never go backwards.

`chat-entered` is still the coarse "the chat is open" signal the legacy hub used, now recorded in the
logical model with a reason — it creates no device row for a message this device never stored. Real
per-message visibility (50 % for 500 ms, invisible mode, a covering screen) is **stage 17**.

## Unread

One counter per membership stint, so "старият архивен unread се изключва от текущия unread" is
structural rather than a special case: `UnreadCountsAsync` only reads the **active** stint's row.

- A new message is an exact **increment** for every eligible recipient, inside the sender's own write
  transaction (nothing else can be true of a message that has just been created).
- Every decrement is a **recomputation** of "permitted, not yet logically seen incoming messages of this
  stint" under the chat lock. Deriving it is what makes "намалява еднократно" true under a retried
  receipt, two devices reporting at once and a restart in between: the value cannot drift.
- Messages read before this stage are respected: the recomputation also treats a legacy
  `MessagesStatus` Seen row as read, so the new model never resurrects them as unread.
- `GET /chat/get-chats` carries `unreadCount`, and **null** (not zero) where no counter exists — a chat
  created before stage 07 has no stint to key one by, and a client must keep its own local count there.

## Group proportions

`IChatAccessAuthority.DescribeParticipantsAsync` answers "every **current** account context that holds,
or ever held, a stint in this chat, with its own windows" — a superseded context (plan/06 fresh start)
is excluded, because it can never read the chat again and counting it would make one person two. The
denominator of a message's proportions is the people whose windows permit **that exact message**, author
excluded: a member added afterwards is not one of them, a removed member whose archive covers it still
is. It is counted per person on both surfaces it travels on:

- the ACK (`new-message-created.RecipientCount`) — the only moment the author learns it, since no later
  event carries it;
- `GET /chat/history` (`recipientCount` per message), resolved at the archive's own snapshot for a
  frozen message.

The numerators are the per-recipient statuses the same page already carried.

**A frozen message's denominator comes from the archive, and the archive has to carry it.** The freeze
(`ChatMembershipService.CaptureArchiveAsync`) therefore writes one `SyncMembershipLegacyStatusSnapshots`
row per person **entitled** to each permitted message at that instant — author excluded — whether or not
that person had reached any status: a row with two null timestamps means "had the right to it, never
reported anything". Anyone who had actually reached a status is kept regardless, because the archive is
evidence. Counting only the pairs the status log happened to carry (which is what the row set used to
be) would make a restored archive claim `1/1 · 0/1` for a message three of four entitled people never
received — and stage 10 removed the reconnect sweep that used to manufacture most of those rows, so the
under-count would be the normal case rather than the exception.

## What the server no longer invents

`BaseHub.OnConnectedAsync` used to mark every undelivered message `Received` for the reconnecting user
and publish that to its authors. Reconnecting proves nothing about a message, and the device whose
connection it is may never have held it — so that sweep is gone. `send-message` likewise no longer turns
presence into a status ("in the chat" → Seen, "online" → Received). Both marks now come from the
recipient device's own receipt.

**Consequence, stated plainly:** a message that was never delivered live stays without a `Received`
mark until a device actually receives and reports it. Catch-up delivery of messages missed while offline
does not exist yet (stages 12/13); what existed before was a mark without a delivery, which is exactly
what this stage forbids.

## Locks and serialization

One order, extending stage 07's: **account → device → session (`TrustedMutation`) → the chat's
`SyncChatSequenceCounters` row (`LockChatAndAuthorizeAsync`) → status/unread rows**. Every status write
re-reads rights under that chat row, so:

- a receipt and a concurrent removal serialize in either order — the loser re-reads the post-removal
  rights and writes only the private device row;
- two devices of one person serialize, so exactly one Seen time is fixed and unread is decremented once;
- a send (several unread rows, ascending membership id) and a receipt (one row) cannot build a cycle.

Verified in SQL against a real PostgreSQL 17 (`/tmp/chatapp-sequential-plans/10-recipient-status/checks`):
two simultaneous Seen receipts leave 1 logical row, 2 device rows, one Seen time and one decrement; a
retry changes nothing; a receipt losing the race to a removal writes only the device row and leaves
unread untouched; a send racing a receipt ends with the new message still unread; reads are never
blocked by a receipt in flight; `pg_stat_database.deadlocks = 0`.

## Migration `20261116120000_AddRecipientStatusAndUnread`

Two additive tables (`SyncLegacyMessageRecipientStates`, `SyncLegacyMessageDeviceStates`) and two
nullable columns on `SyncUnreadStates` (`OldestUnreadLegacyMessageId`, `LatestUnreadLegacyMessageId`).
No existing column changes and **no backfill**: the legacy `MessagesStatus` log keeps its meaning and is
still written, as the projection the current UI and the frozen-archive snapshot read.

Unread deliberately did **not** get a legacy twin table the way stage 08's snapshots did: a stint has
exactly one counter, and a second table would mean two. The composite foreign keys pin each row to a
membership that really belongs to both that chat and that account, and a device row to an instance that
really belongs to that registration and account. Every constraint and index name is explicit and within
PostgreSQL's 63-character limit (longest 61), unlike the inherited stage-02 names.

Every foreign key carries its own index, including
`(DeviceRegistrationId, LocalInstanceId) → SyncLocalInstances`, which the primary key does not cover —
declared explicitly rather than left to EF's `ForeignKeyIndexConvention`, whose conventional name here
would be 69 characters and which would otherwise put an index in the runtime model that the migration
and the model snapshot do not describe.

The legacy-message foreign key is `Restrict` for the **public** rows — `SyncLegacyMessageRecipientStates`
and the `SyncUnreadStates` pointers — so a legacy message cannot be deleted while evidence about it
exists; a future edit/delete stage must clear them in the same transaction. It is `Cascade` for the
**private** `SyncLegacyMessageDeviceStates`: that row is one device's own progress on a message, and
nothing is lost by it going with the message.

## Later-stage obligations

1. **Catch-up delivery (stages 12/13).** Messages missed while offline are still never delivered; the
   honest `Received` state above makes that gap visible instead of masking it.
2. **Offline Seen and the clock (stages 14/17).** The contract accepts a client's own observation time
   only on the strength of reliable **server-authenticated** samples before the disconnect and after the
   reconnect. No surface carries such samples yet, so the server records its own receipt time and keeps
   the client's reported instant separately (`SeenClientReportedAtUtc`). Stage 14/17 must add the sample
   mechanism before that time may become authoritative — accepting an echoed timestamp today would let a
   device name any moment it likes.
3. **Per-message visibility (stage 17).** `chat-entered` marks everything permitted; the 50 %/500 ms
   rule, invisible mode and the covering-screen rule are not implemented here. The `Seen` receipt kind
   is the surface stage 17's client will use.
4. **Sequence-space rows (stage 11+).** `SyncMessageRecipientStates`/`SyncMessageDeviceStates` and the
   `SyncUnreadStates` sequence pointers stay unwritten until `SyncMessages` exist; stage 11 must write
   them alongside (or instead of) the legacy rows, and must journal status changes as `SyncChatChanges`
   (stage 10 writes no journal entry for a legacy status change — the live event is today's delivery).
5. **Operation idempotence (stage 11).** The `OwnSend` boundary is the committed message's own instant.
   Once operations carry a `ClientOperationId`, a retried send must reuse the original result and
   therefore the original boundary.
6. **Pre-stage-07 chats.** A chat with no membership projection has no stint to key a logical row by, so
   a receipt there still writes only the legacy `MessagesStatus` projection (idempotently, monotonically
   and with a derived `StateVersion`) and `unreadCount` is null. The first membership mutation projects
   such a chat, after which the full model applies.
7. **The receipt queue (stage 16).** The hub answers every batch with its per-message outcome; acting on
   it — holding a receipt until `Applied`, dropping it on the terminal `NotApplicable`, surviving a
   restart — is the client-side queue stage 16 builds. Nothing retries indefinitely today, because
   nothing retries at all.
8. **Archives frozen before this stage.** The entitled-recipient rows above are written by the freeze,
   so an archive frozen by an earlier build carries only the pairs its status log had. The migration is
   additive with no backfill (no released data exists); such an archive under-reports its denominator
   until the stint is frozen again, which cannot happen for an already-frozen stint.
