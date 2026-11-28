# v1 receipts: CiphertextStored and Received (part 2)

`plan/10-recipient-status.md`, in the SEQUENCE space this time. Stage 10 built the legacy-shaped status
model; stage 11 started committing `SyncMessages`; stage 12 delivered them. This part adds the surface a
device uses to say what it actually holds of a v1 message, and the delivery of the status changes that
produces.

Scope is deliberately two kinds: `CiphertextStored` and `Received`. **`Seen` is refused**
(`InvalidRequest`) — fixing a logical Seen time, its reason and an unread decrement needs the clock
rules and a column for the client's original observation instant that `SyncMessageRecipientStates` does
not have yet. Accepting it here would durably record a time the later parts could not correct, so it is
refused explicitly rather than half-supported.

## The wire is singular

`BaseHub.sync-receipt` takes **one** JSON-string `ReceiptRequest` and answers **one** JSON-string
`ReceiptResult` — or a `SyncFailure`. It is not a batch surface: `SyncProtocol.MaxReceiptBatchItems`
(50) bounds a batched receipt endpoint and this is not one, so the only limit that applies is
`SyncProtocol.MaxHubMessageBytes` (32768), enforced on the real UTF-8 wire bytes **before** the payload
is parsed. The encoding is `SyncProtocol.CreateJsonOptions()`, like every other v1 hub method, never the
legacy MessagePack resolver.

The legacy `message-receipt` method (legacy int message ids, batched) is untouched and still live. The
two surfaces cannot cross over: one speaks legacy integer ids, the other v1 lowercase UUIDs.

## What the server decides

```
ReceiptRequest { protocolVersion, operation{device,clientOperationId}, scope{chatId,membershipId,rightsVersion}, messageId, kind, observation:null }
```

1. `protocolVersion` must be 1, before anything else — "Unknown protocolVersion fails before mutation".
2. Shape: every id is an `[A-Za-z0-9_-]{1,128}` token, `rightsVersion` a canonical decimal, `chatId` the
   legacy chat id as a decimal string, `messageId` a **canonical lowercase** UUID, `observation` null.
   The casing is enforced, not assumed: `Guid.TryParseExact(…, "D")` is case-insensitive, so the id is
   round-tripped through `Guid.ToString("D")` and compared ordinally. An uppercase UUID is therefore
   `InvalidRequest` — a malformed request the client can fix — and never a terminal `NotApplicable`,
   which is what "no row matched" would otherwise have made a client casing bug look like.
3. `TrustedMutation` — the device/session row is locked inside the transaction that will write, so a
   concurrent revocation serializes against this receipt instead of racing it.
4. `SyncCallerIdentity.ResolveAsync` — account, registration, local instance and session are re-read
   from the rows they will be used with, and `operation.device` must equal that tuple **exactly**. A
   receipt naming another account, registration or SQLite copy is refused, never re-attributed.
5. `LockChatAndAuthorizeAsync` — the chat's own `SyncChatSequenceCounters` row, i.e. the same point every
   membership mutation and every send serializes on; it also applies the plan/06 fresh-start boundary.
6. The **named** stint must exist and belong to this account.
7. `rightsVersion` may be **lower** than the stint's current one — an offline receipt carries the version
   of its enqueue moment and that is normal. Only a HIGHER one is refused, as `RightsChanged`: the client
   claims rights the server never issued, so it reconciles rather than dropping anything.
8. The message must exist in that chat and must lie inside that stint's own
   `SyncMembershipHistoryGrants` ranges. **Sequence grants of the named stint**, not the union of the
   person's windows and not `ChatAccess.WindowAt` — after a rejoin with an overlapping period two stints
   mean different things about the same message, and the device said which one it is reporting under.

## Dispositions

| Answer | When | StateVersion |
| --- | --- | --- |
| `Applied` | something actually moved | the device row's `Version` |
| `AlreadyApplied` | everything was already recorded | the same `Version` |
| `NotApplicable` | **permanently** irrelevant: no readable chat, no such stint of this account, no such message, or a message outside the named stint's grants | null (terminal) |
| `SyncFailure` | session expired/untrusted, diverged rights, malformed request | — |

`Applied`, `AlreadyApplied` and `NotApplicable` all finish the queued operation. `NotApplicable`
finishes it without applying a status because the named scope no longer authorizes the receipt.
An authorization or infrastructure failure is a `SyncFailure`, so the device keeps the receipt
in its queue — nothing is dropped because the server was unavailable or the token had expired.

## What it writes

**Private device progress** — `SyncMessageDeviceStates`, keyed by (message, registration, local
instance). Monotone: a timestamp that is set is never moved, `Received` back-fills
`CiphertextStoredAtUtc`, and the row's `MembershipId` is fixed by the FIRST receipt and never rewritten,
so a later receipt naming a rejoined stint cannot silently move the old stint's archive progress.

**The person's public mark** — `SyncMessageRecipientStates.ReceivedAtUtc`, written only when all of
these hold: the kind is `Received`, the named stint is **Active**, the message is not the caller's own,
and no row of this person for this message carries a `ReceivedAtUtc` yet (one person, one first proof,
whichever stint or device produced it). The row written is the **named stint's own**; another stint's
row is never rewritten.

**One person, one public state — folded, not picked.** The table is keyed by
(`MessageId`, `MembershipId`), so one person can hold several rows for one message: a rejoin gives them
a second stint, and the OwnSend pass (`MarkOwnSendSeenInSequenceSpaceAsync` →
`SyncRecipientStateAsync`) creates and marks the **current** stint's row for every older incoming
message, independently of the receipt guard above and of what an older stint's row already says. That
is a normal data shape, not a defect, and it is why `PublicRecipientState.Fold` — one shared helper in
`Services/Statuses/Models` — is what every public reader answers from:

| Field | Rule |
| --- | --- |
| `ReceivedAtUtc` | the **earliest** non-null across the person's rows |
| `Seen` | the **earliest** Seen instant and *that* row's reason — the first accepted transition owns both |
| `Version` | the **sum** of the rows' versions |

The sum is the monotone choice. Each row's `Version` only increments, no writer deletes a row, and a
new row starts at 0 — so the aggregate never decreases and it moves whenever *any* contributing row
moves. An arbitrary row's version regresses the moment the chosen row changes, and a maximum silently
swallows an increment made on the other row.

Both readers use it: `SyncProjection.RecipientStatesAsync` (live scopes — it emits one record per
*person*, not per row) and `ChatMembershipService`'s frozen capture, so an archive freezes exactly the
state the chat was showing. Frozen snapshots themselves stay immutable: nothing here ever writes to
`SyncMembershipMessageStatusSnapshots`, and a frozen scope reads its own rows, which are already one
per (membership, message, recipient) and need no fold.

Consequences that are enforced structurally rather than by convention:

- a **frozen** stint writes the private row only — "Публичните отметки и unread в замразения чат остават
  непроменени" — which is exactly how a removed member confirms downloading its archive;
- a **rejoin overlap** does not lose the public mark: the two writes are independent, so a receipt that
  finds the device row already complete (`AlreadyApplied` for the device) still creates the public mark
  the new active stint owes;
- nothing here touches `SeenAtUtc`, `SeenReason` or any unread counter. `Received` is not an
  observation, and unread recomputation stays Seen-only.

## Delivery of a status change

Each committed change appends ONE `SyncChatChanges` entry and — when the gate below is on — ONE
`SyncOutbox` row at that position. The journal allocation is free of extra contention: the chat counter
row is already held by step 5 above.

The outbox row carries its **own** `ChangeKind` and `SecondaryId`. That is not duplication: the journal
is a liveness/cursor mechanism that `SyncRetentionService` prunes after 30 days, and a delivery task
that waited longer than its journal entry survived must still know what it delivers and to whom. The
broker payload is unchanged — `LiveDeliveryService` re-reads the row by `task.OutboxId`, verifies that
the task's chat/message/change-sequence match it, and then switches on the persisted kind.

| Kind | `SecondaryId` | Audience |
| --- | --- | --- |
| `Message` | null | unchanged: every reauthorized account's trusted registrations, with the key grant |
| `RecipientState` | the subject account context | every account whose rights to **this message** are current right now, **excluding frozen stints** |
| `DeviceState` | the reporting **local instance** | that one SQLite copy, via `sync-instance:{localInstanceId}` — **nothing at all** when the reporting stint is frozen |

- `SecondaryId` holds exactly ONE id because a pointer at the row that changed needs one reference, not
  because of width: the value is always copied from a row the server already resolved, and every
  persisted id column is `varchar(100)` like this one. A *pair* is what would not fit, which is why the
  owning registration is read back from the device-state row instead of being stored next to the
  instance id.
- A frozen archive never receives a status change produced after it was frozen — its content is resolved
  at its own snapshot, and post-freeze updates are outside its access. This holds for the **private**
  `DeviceState` event too, which is the common case for an archive: a removed member's device confirms
  the archive it just downloaded, one receipt per message, and every one of those changes is allocated
  **after** the stint's frozen bound (`SyncMembershipMetadataSnapshots.CapturedAtChangeSequence`,
  stamped at freeze time and fixed forever). An event carrying such a position is structurally
  unappliable: a client applies a live event only at `applied + 1` and otherwise flips to catch-up, and
  a frozen scope's catch-up is bounded by that same fixed position, so it would never arrive — costing
  a readiness flap and a wasted HTTP pass per message. Worse, one landing exactly at `bound + 1` *would*
  be applied and persisted, and the next `Resume` submitting a position above the boundary is answered
  `InvalidRequest` for the whole batch, which retrying cannot clear.
  `DeliverDeviceStateAsync` therefore answers `Delivered(0)` for a frozen stint. The receipt is
  unaffected: the private row, its journal entry and the `ReceiptResult` all commit exactly as before,
  and the outbox row (if the gate queued one) is consumed as a no-op, so rows already queued by an
  earlier build drain the same way.
  This check uses the device row's membership, pinned by its first receipt. If the same copy
  later reports `Received` after rejoining, a row pinned to the old frozen stint still has no
  live device-state echo. Its receipt commits normally; HTTP delta can return the private state
  under the new active stint when that stint's grants cover the message.
- Private progress is delivered to the exact instance, never to the registration group: a phone that
  cleared its local database has a second instance under the same registration, and that copy's progress
  is not this one's. The group is joined in `OnConnectedAsync` (and re-asserted in `sync-handshake`) from
  the connection's own claims.
- The HTTP delta applies the same narrowing: a `DeviceState` journal entry is resolved only for the
  local instance named by the row's own `SecondaryId`. Every other caller — including another copy of
  the same account — gets an empty group at that position. Without that filter a second copy would be
  handed a change describing *its own* row at a position where a different copy's row moved: no leak,
  since it is the caller's own data and the `EventId` comes from the caller's own version, but a
  spurious "something changed" about a row that did not change.
- A status task never unwraps a content key. The key is materialized only on the `Message` path.
- Event ids come from `SyncChangeFactory`/`SyncEventId` — derived from the state's own version — so a
  live status event and the same change arriving later in an HTTP delta page deduplicate against each
  other.

After the commit the hub wakes this replica's publisher (`IOutboxPublishSignal.Notify()`) and only then
returns the answer: a device that receives a `ReceiptResult` knows the state it describes is durable.

## Deployment ordering, and the gate

The two columns are additive (`20261127120000_AddReceiptOutboxDiscriminator`, `ChangeKind` default
`1` = `Message`, nullable `SecondaryId`), but the **meaning** of an outbox row is not backward
compatible: an older consumer treats every claimed row as a message delivery and would re-ship the
message body and its content key — under a new `EventId`, so client-side dedupe would not stop it.

Required order:

1. apply the additive migration;
2. deploy the new consumer (this build) to **every** replica;
3. only then set `Delivery__PublishStatusChanges=true`.

`DeliveryOptions.PublishStatusChanges` defaults to **false**, and this is the whole of the mixed-version
story — there is no other guard, so do not treat a half-upgraded fleet as safe without it. With the gate
off, receipts are still accepted, the state and its journal entry still commit and the answer is still
`Applied`; only the live hop is skipped, and every device still receives the change through the HTTP
delta, which reads the journal rather than the outbox. Rolling back the columns while status rows exist
would erase their delivery meaning, so roll the gate back first and let the queue drain.

## Known consequence: the chat stream has private holes

**Stated explicitly so it is a recorded decision, not a surprise.** `SyncChatChanges` is ONE sequence
per chat, shared by every member and every device, but a status change is not visible to everyone in it:

- a `DeviceState` position belongs to one SQLite copy. Nobody else may resolve it — `SyncPageService`
  answers an **empty group** at that position for every other caller (now filtered on the journal row's
  own `SecondaryId`, so even another local instance of the same account gets the empty group), and live
  delivery addresses only that one instance (and, per the previous section, nobody for a frozen stint);
- a `RecipientState` position is resolvable by everyone still entitled to the message, but with the gate
  **off** — the production default — no status change is delivered live at all.

So a device's `appliedThroughSequence` sits below the chat's head after any status change it cannot see,
and the next live `Message` event arrives at a position `> applied + 1`. The client treats that as a gap:
it stops applying live events and runs an HTTP catch-up pass, which coalesces the skipped positions
(empty groups included) and then resumes live. Correctness is unaffected — the page is the authority and
empty groups advance the cursor without revealing anything — but with receipts flowing, "live delivery
degenerates into a catch-up round per message" is the steady state rather than the exception, multiplied
by devices × messages.

This is **pre-existing architecture, not something this part introduces**: `MessageAcceptanceService`
already journals `Unread` and OwnSend `RecipientState` positions that most members cannot resolve. The
receipt surface makes it common. Narrowing the hole properly means separating the private/per-person
streams from the chat stream (or giving the catch-up a cheaper "nothing for you here" answer), which is
a stream-model change and is deliberately **not** attempted here.

## Not in this part

- **`Seen`**, the clock/offline-sample rules and any unread movement — refused here on purpose.
- **Legacy-message receipts** over the v1 surface: `sync-receipt` answers about `SyncMessages` only.
- **The client queue.** Draining, backoff and the "drop on `NotApplicable`" rule belong to the client
  side of stage 16; this part owes it a final, terminal answer and gives one.
- **Unread/`Membership` outbox kinds.** Only `RecipientState` and `DeviceState` are produced; any other
  kind on an outbox row is answered `Unprocessable` with its reason rather than guessed at.
