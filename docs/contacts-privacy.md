# Personal contacts and profile visibility, server side (stage 09)

Stage 09 only (plan/09-contacts-privacy.md). Adds `Services/Contacts/` — the single owner of what is
between two people — and `Services/Authorship/` — the author rights a future edit/delete will need. It
wires both into the entrypoints that exist today: `GET /friends`, `POST /friends/add/{phone}`,
`POST /friends/request-respond`, the new `POST /friends/block|unblock|remove`, `GET /chat/get-chats`,
`GET /chat/history`, the hub's `send-message`, and the push fan-out.

Stage 07's `IPersonalBlockDirectory` said this would happen here ("Stage 09 owns the full personal-block
model … this interface is the single place that would change"); it is now a thin adapter over the new
directory, so the membership writer's add rule is unchanged.

## The durable model

`SyncPersonalBlocks(BlockerUserId, SubjectUserId, CreatedAtUtc, LiftedAtUtc?)`, primary key on the
**ordered pair**. The legacy `Friendships` row has no blocker column — a row could only reach
`Blocked` through `POST /friends/request-respond`, whose caller is always the row's `TargetUserId` — so
it can express neither "the request's SENDER blocked the other party" nor a mutual block. This table
does both.

- Keyed by **legacy user ids**, like the friendship it overlays, so a fresh start (plan/06: a new
  `AccountContextId` on the same phone and the same `Users` row) keeps a block exactly as it keeps a
  friendship. Starting over is not a way to shed a block, in either direction.
- One row per ordered pair forever: a re-block reuses it (`LiftedAtUtc` back to null). That is what
  makes block/unblock converge after a lost response and a retry.
- **No backfill.** Legacy rows that already reached `FriendshipStatus.Blocked` are unioned in by
  `PersonalRelationDirectory`, with stage 07's derivation (blocker = `TargetUserId`). Lifting such a
  block also restores that row to `Accepted`, or the legacy half would stay in force.

**`Friendships.Status` keeps its original meaning** (the friend-request lifecycle). A block is a
separate directional overlay, projected onto what a client sees by
`PersonalRelationProjection.Project`. That is what makes "Отблокирането възстановява достъпа за новата
кореспонденция" exact: unblocking removes the overlay and the underlying `Accepted` relation applies
again, with no "previous status" to store and get wrong. **Removing a contact** is the legacy
`Rejected` status, now writable by *either* party — which is also what already makes "Премахнат контакт
може да бъде добавен повторно" true in `AddFriend`.

## The correspondence gate

`ChatAccess` gains `PersonalCorrespondenceSuspended` and `CanSendNewMessages`
(`State == Active && !PersonalCorrespondenceSuspended`). Only **creating new content** is closed:

| | before a block | after a block |
| --- | --- | --- |
| `CanRead` / history | true | **true** |
| `CanSend` (active stint: `chat-entered`, the reconnect sweep, presence, Seen) | true | **true** |
| `PermitsMessageAt` for the existing messages | true | **true** |
| `CanSendNewMessages` (`send-message`, the chat list's composer) | true | **false** |

That split is the stage's requirement, not an accident: "Разрешените стари съобщения остават достъпни и
продължават да получават статусни промени."

`ChatAccessAuthority` consults the directory only for a **personal** chat — a projected chat whose
persisted `SyncChats.Kind` is `Direct`, or (for a chat created before stage 07, which carries no kind) a
legacy chat with exactly two participants, the same rule its creation applied. A group is never gated:
"Личното блокиране запазва активното участие и кореспонденцията в общи групи." The counterpart is read
from the legacy participant list, which never changes for a personal chat and survives everything.

What suspends it: either side holds an active block, **or** the two are not currently accepted contacts
(a removal). A personal chat could only ever be created between accepted friends, so "no row" means the
relation was ended.

## Profile visibility

`IProfileVisibility` is the only resolver. A subject is **known** to a viewer when

1. it is the viewer itself; or
2. the viewer **actively blocks** the subject — a viewer must be able to read its own block list, which
   is the only place a block can be lifted; or
3. a `Friendships` row exists between them with status `Pending` or `Accepted`, **and** the subject does
   not actively block the viewer.

Everyone else reads `ProfileIdentity.Unknown` — the literal name `"unknown"` and an empty phone: a
stranger sharing a group, a removed contact, a rejected request, and a party that has been blocked. It
fails closed (an unresolvable or non-existent id answers Unknown rather than being skipped), and it is
deliberately **directional**: after A blocks B, A still sees B (its own block list), B sees `unknown`.

`Pending` discloses on purpose: a friend request is an explicit disclosure by whoever typed that phone
number, and its recipient needs the identity to answer it at all.

`ViewersWhoKnowAsync` is the transposed question, for the push fan-out: one message, many recipients,
one sender — asking per recipient would be a pair of queries per device.

### Where the answer is actually used

| Surface | What changed |
| --- | --- |
| `GET /friends` | names/phones resolved; the block projected over status and `isInitiator`; a block with **no** friendship behind it is listed too (otherwise it could never be lifted); the caller's status/role filters applied **after** all of that |
| `friend-request` event + its push | the sender's identity resolved from the RECIPIENT's point of view |
| `personal-relation-changed` (new) | both parties, each with its own copy — the same projection `GET /friends` returns |
| push (`NotificationForNewMessage`/`NewChat`) | the audience is split once into "knows the sender" and "does not", so one group message is titled with the name for some recipients and `unknown` for the strangers in it |
| push (accept/reject/block) | resolved for that one recipient; a blocked party therefore reads `unknown`, never the blocker's name |

`GET /chat/get-chats`, `GET /chat/history` and the hub's message/membership events carry **no profile
data at all** — only numeric user ids — which is why nothing had to be stripped there. Stage 07 already
withholds the actor from a `Removed` subject's copy of a membership change, and a frozen metadata
snapshot names participants by id; both are unchanged and both now have the resolver available for any
surface that renders those ids.

## Prepared author rights (`Services/Authorship/`)

`IMessageAuthorshipAuthority.RevisableByAuthor` answers "may this caller, from this device, later
edit/delete this message": the message is its own, it is still inside a window this caller holds —
**including a frozen archive**, the case the plan names explicitly — and the caller carries its
account/device claims (the stage-05 `AppSessionRequirement` has already re-checked trust for this
request). `GET /chat/history` returns it per message as `canAuthorRevise`.

The operations themselves are a later stage. When they arrive they must ask this authority **and**
re-validate trust at the moment of the write through `TrustedMutation`, exactly like every other
mutation here: a read-time answer is not a write-time authorization.

## Locks, concurrency and recovery

- **No new lock and no new lock order.** The relation writer takes no chat row and allocates no
  sequence; it stops at the device/session rows `TrustedMutation` already locks (account → device →
  session), so it cannot invert stage 07's account → device → session → chat → catalog order.
- **A send racing a block** has exactly one serialization point, the one stage 07 built:
  `BaseHub.SendMessage` calls `LockChatAndAuthorizeAsync`, which takes the chat's
  `SyncChatSequenceCounters` row `FOR UPDATE` inside the write transaction and then re-reads the rights
  — now including the block. Either the message commits before the block is visible, or the send waits
  and is then refused. Verified in SQL: a committing block does not delay an unrelated rights read
  (14 ms while the block was uncommitted), and no deadlock occurs.
- **Blocking is one conflict-tolerant statement**, not read-then-insert. `TrustedMutation` locks the
  caller's *device* row, which serializes two attempts from the same device but **not** two devices of
  one account, nor a retry overlapping its own first attempt. Reproduced in SQL: the read-then-insert
  shape really does fail with `duplicate key value violates constraint "PK_SyncPersonalBlocks"`, while
  `INSERT … ON CONFLICT … DO UPDATE` leaves exactly one active row. `CreatedAtUtc` is preserved while a
  block is already in force, so a retry cannot move the moment it started; only re-blocking a lifted row
  re-dates it.
- **Unblocking is a conditional `UPDATE`** (`… AND "LiftedAtUtc" IS NULL`), so two concurrent lifts
  converge on one committed time and neither can fail.
- **Broadcast strictly after commit**, like every other mutation here. A crash between commit and
  broadcast loses only the live event; the next `GET /friends` + `GET /chat/get-chats` re-derive the
  whole state from committed rows. Nothing in this stage is keyed by session, connection or sync
  session, so a reconnect invalidates nothing.
- **No authorization change.** Every endpoint is on the existing `[Authorize]` controllers (the stage-05
  `AppSessionRequirement`, re-checked per request), every write re-validates trust while holding the
  device row, and a refusal is a plain refusal.

## Migration `20261115120000_AddPersonalBlocks`

One additive table, no existing column changes, no backfill. Apply it with the usual dedicated step
(`dotnet Turbo.dll --migrate-only`, see docs/server-data-migrations.md). Unlike the inherited stage-02/08
names, every constraint and index name here fits PostgreSQL's 63-character identifier limit (longest: 49),
verified in SQL.

Verified against a real PostgreSQL 17 with hand-transcribed DDL (no application and no .NET compilation —
this stage ships under an explicit no-build restriction): the ordered pair is one row, the opposite
direction is a second row, a self-block and a block of a non-existent user are both refused, lifting and
re-blocking reuse the row, the "who has blocked me" direction uses the secondary index, a legacy
`Blocked` row still reads with the stage-07 derivation, the union of both sources is what the directory
computes, a removal flips the status without touching any block row, and deleting a user leaves nothing
dangling.

## Later-stage obligations (not defects of this stage)

1. **Push body (stage 19).** "Тялото е „Private“ или еквивалентен общ текст" — this stage makes the push's
   *profile data* follow the unknown rule; the body is still the message text.
2. **Profile photo.** `UserEntity` has `Name` and `Phone` only, so "снимка" has nothing to hide yet.
   `ProfileIdentity` is the single place a later photo field is added.
3. **Edit/delete (future stage).** `IMessageAuthorshipAuthority` is the gate those operations must pass,
   plus a `TrustedMutation` re-check at the write. Nothing is stubbed.
4. **Sync-protocol surfaces (stages 13/15).** The v1 wire contract carries no profile data, so when a
   future surface does render identities it must route them through `IProfileVisibility` rather than
   reading `Users` directly.
5. **Cross-replica fan-out (stage 21).** `personal-relation-changed` is published through the same
   per-process hub context as every other live event.
