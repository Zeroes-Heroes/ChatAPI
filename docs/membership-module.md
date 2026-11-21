# Chats, membership and access, server side (stage 07)

Stage 07 only (plan/07-membership.md). Adds `Services/Membership/` - the single writer of the
chat/membership model and the single shared access check behind every reachable surface - and wires it
into the entrypoints that exist today: `POST /chat/create-chat`, the new `POST /chat/add-members`,
`POST /chat/remove-member`, `POST /chat/leave`, `GET /chat/get-chats`, the hub's `send-message`,
`chat-entered` and its reconnect sweep, and the push fan-out. Key grants are stages 11-13.

> Stage 08 extends this module: `POST /chat/add-members` now carries the shared period, `FreezeAsync`
> captures the archive's metadata/status snapshots, and `ChatAccess.Windows` folds in the shared windows.
> See `docs/history-archives.md`.

## Chat identity and kind

A chat's sync id is the decimal string of its legacy `Chats.Id` (docs/sync-contract-v1.md: "Existing
numeric entity IDs become decimal strings"), so no mapping table exists and a group keeps **one**
ChatId across every composition change - including down to one or zero active participants.
`SyncChats.Kind` is decided once, at creation, from the participant count (exactly two = `Direct`,
more = `Group`) and never recomputed: a group reduced to two participants is still a group, a personal
chat never becomes one. The count is taken over the **canonical** participant set - `ChatService.
CreateChat` distincts the requested ids once and uses that one array for the two-friends rule, the
creation guard, the projection and the push audience - so a request cannot pad its list with a repeated
id to be treated as a group by the friendship rule and as a personal chat by everything else. Every id
in that set must also be a real `Users` row: an unknown one is refused with 404 *"Unknown participant."*
(the same answer `AddMembersAsync` gives), because the projection provisions one account context per
participant and would otherwise break that insert's foreign key mid-transaction. `GET /chat/get-chats` now returns that kind (`isGroup`) and whether the caller
holds the chat only as a read-only archive (`isArchived`), so no client has to guess from the
participant list.

Chats created **before** this stage have no projection. The first membership mutation on such a chat
projects it from the legacy participant list (under a per-chat advisory lock, so two concurrent first
mutations cannot both insert it), with every existing participant getting full history of the chat.
Until then `IChatAccessAuthority` answers for them exactly as the pre-stage-07 code did: a legacy
participant is an unrestricted active member, a stranger has nothing.

## The one shared access check

`IChatAccessAuthority` is what HTTP, socket and deferred delivery all ask. It composes the stage-06
`IAccountContextBoundary`, so one call answers both "is this chat part of this account context at all"
and "what does this context's membership permit":

- `Active` - may read current content and publish (including the single remaining member of a group).
- `Frozen` - read-only archive of what each ended stint permitted; also what every participant of a
  group with zero active members has.
- `None` - no stint ever, or a chat belonging to a superseded account context.

`ChatAccess.Windows` is the **union** of each stint's permitted window, which is how a rejoin is
represented: the old frozen archive plus the newly shared period, with the gap in between covered by no
window at all. `PermitsMessageAt` is the per-message gate the reconnect sweep and `chat-entered` use, so
a participant added later never reaches back before its own window and a removed one never reaches past
its removal.

Windows are expressed in **time**, not in MessageSequence, because the only messages that exist today
live in the legacy `Messages` table, whose rows carry `CreatedAt` and nothing else
(`SyncMemberships.LegacyHistoryFromUtc` is the lower bound, `EndedAtUtc` the upper one). The
sequence-space ranges are written in parallel on `SyncMembershipHistoryGrants` by the same transaction,
which is what stage 08/13 will read once messages are sequence-numbered.

**Message keys**: the same answer is the gate a key grant must pass. Stage 07 has no key-delivery path
at all (`IMessageKeyProvisioningService.UnwrapForAuthorizedDelivery` still has no production caller), so
this stage deliberately stubs nothing: stages 11-13 must call `GetForAccountAsync`/`GetAsync` and check
`PermitsMessageAt` before unwrapping a key. That is a recorded obligation, not a protection this stage
claims to provide.

## Why a removed participant keeps its legacy row

Removing or leaving does **not** delete the legacy `ChatsUsers` row: that row is what keeps the
archive readable at every legacy surface (`GET /chat/get-chats`, the message/status queries). The
consequence is that the legacy participant list means "ever a participant", so nothing may use it as a
rights list any more - which is exactly why every surface now goes through the authority:

| Surface | Before | Now |
| --- | --- | --- |
| `GET /chat/get-chats` | legacy participants | membership: active set, or the composition as of this caller's own removal |
| `send-message` | legacy participant | active stint, re-checked **inside** the write transaction under the chat lock |
| recipients / push | legacy participants | `ActiveLegacyRecipientsAsync` (current contexts with an active stint) |
| reconnect sweep | every undelivered message | only messages this membership's windows permit |
| `chat-entered` | marks everything Seen | a frozen archive marks nothing and registers no presence |

## Locks and serialization

One order everywhere: **account → device → session** (stage 05 `TrustedMutation` /
`CheckAndLockDeviceForMutationAsync`) **→ chat** (`SyncChatSequenceCounters` row) **→ the touched
accounts' `SyncAccountCatalogCounters` rows, in ascending legacy user id**. Every stage-07 mutation runs
inside `TrustedMutation`, so a concurrent revoke of the caller serializes against the write and the
broadcast only ever runs after a successful commit.

- **Several accounts in one transaction.** This is the first module that locks more than one
  account-scoped counter at a time (an add with several targets, and the projection of a chat with
  several participants). Taking them in the order the request happened to list them deadlocks two
  mutations on *different* chats - the chat row does not serialize those - whose target lists overlap in
  the opposite order. Both multi-account loops therefore iterate a `Distinct().Order()` set:
  `AddMembersAsync`'s targets and `ProjectAsync`'s members. Reproduced and re-checked in SQL against a
  real PostgreSQL: client-supplied order deadlocks (`40P01`), ascending order does not.

- **Rights vs chat writes.** A membership mutation allocates its change sequence with
  `SyncSequenceAllocator.AllocateChangeSequenceAsync`, which holds the chat's counter row until commit.
  `BaseHub.SendMessage` takes the same row with `SELECT … FOR UPDATE` (no allocation) inside its own
  write transaction and re-evaluates the publish right under it. A concurrent send and removal
  therefore have exactly one serialization point, in either arrival order, with no lock-order inversion
  (verified in SQL against a real PostgreSQL: both orders block and then proceed, zero deadlocks).
- **Duplicate exact participant set.** Creation takes `pg_advisory_xact_lock` over a key derived from
  the canonically sorted participant list and only then looks for a chat whose **current active** set
  equals it. Two concurrent creations of the same set cannot both pass; a key collision costs extra
  serialization, never correctness; advisory locks are cluster-wide, so this holds across API replicas.
  There is deliberately **no** unique index over the composition: a group keeps its ChatId through
  composition changes, so a durable constraint on a mutable set would reject legitimate removals. The
  durable constraint that does exist is the one that must never be violated:
  `IX_SyncMemberships_ChatId_AccountId_Active` (unique, `WHERE "State" = 0`) - an active participant can
  never be added again to widen its history, not even under a concurrent double add.
- **Refusals roll back.** The inner, transactional part of a mutation refuses by throwing
  (`ChatMembershipRefusedException`), so a refused attempt cannot leave half a projection or a journal
  entry behind.

## Idempotence and recovery

- Adding an already-active participant is a conflict (never a second stint). Removing someone who is
  already frozen, or leaving a chat already left, answers with the current state and writes nothing -
  so a lost response followed by a retry converges instead of producing a second, differently-labelled
  outcome. Nothing is broadcast twice.
- A crash between commit and broadcast loses only the live event; the next `get-chats` re-derives
  membership from the committed rows.
- `MembershipId` is a fresh lowercase UUID per stint: a rejoin never reopens the old one, so its frozen
  archive and its new rights stay separate records.
- A removal/leave records who ended it (`EndedByAccountId`, null for a voluntary departure) and an
  addition records who added it (`AddedByAccountId`, new in this stage), which is what lets the wire
  `MembershipEvent`'s actor be derived per recipient - and withheld from a removed subject - without a
  second copy on the change journal.

## Blocking

`IPersonalBlockDirectory` answers one question: which of these participants have blocked the actor.
Direction comes from the legacy `Friendships` row, which has no blocker column: a row can only reach
`Blocked` through `POST /friends/request-respond`, whose caller is always the row's `TargetUserId`, so
the blocker is `TargetUserId` and the blocked party is `SenderUserId` (the client's blocked-contacts
list reads the same relation the same way). Only the specified direction is enforced - the blocked party
cannot add its blocker, while any other participant still can. Stage 09 owns the full personal-block
model and is the one place this derivation would change.

## Fresh start (stage 06) interaction

Membership decisions always key on the caller's own `AccountContextId` claim, never on the legacy user
id. Two further rules keep a superseded context's membership from reattaching to its successor:

- projecting a pre-stage-07 legacy chat **skips** any participant whose current context is excluded
  from that chat (the legacy row belongs to the context it replaced);
- adding such a participant is refused rather than silently granted-and-ignored.

The cost is explicit: a phone that started over cannot be re-added to a chat it took part in before,
because the exclusion snapshot still names that chat. Re-admitting such a context requires revisiting
the exclusion itself, which belongs to the stage that owns it, not to an ordinary add.

## Migration

`20261113120000_AddChatMembershipModel` is additive and touches one existing table
(`SyncMemberships`, created by stage 02 and never written to before this stage): two nullable columns
(`AddedByAccountId`, `LegacyHistoryFromUtc`) and the filtered unique index above. No backfill is
required - null means exactly what a pre-stage-07 row means. Apply it with the usual dedicated step
(`dotnet Turbo.dll --migrate-only`, see docs/server-data-migrations.md).

Verified against a real PostgreSQL 17 with hand-transcribed DDL (no application or .NET compilation -
this stage ships under an explicit no-build restriction): the filtered unique index rejects a second
active stint while allowing any number of frozen ones, an empty permitted range is not representable
(which is why freezing a stint that was granted no committed message deletes its range row instead of
storing an impossible one), and the frozen-at position must be a real change sequence.
