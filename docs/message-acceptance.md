# Message acceptance (stage 11)

`plan/11-message-acceptance.md`. The v1 `SendText` operation: one PostgreSQL transaction writes the
message, its protected key, the operation result, the OwnSend changes, the journal and the outbox, and
the server ACK is this invocation's own return value, sent after the commit.

## Who owns what

| Module | Owns |
| --- | --- |
| `Services/Messaging` | the acceptance decision and the operation-identity lookup (`IMessageAcceptanceService`) |
| `Services/Authorship` | the author-key registry (`IAuthorKeyRegistry`) - which SPKI a reference is bound to |
| `Services/Crypto` | JWS parsing, ES256 verification, `PackageDigest` |
| `Services/Keys` | wrapping the transient message key for storage (first production caller) |
| `Services/Membership` | rights, the chat-row lock, the lazy projection of a pre-stage-07 chat |
| `Services/Statuses` | unread and the OwnSend marks of the accepted message |

## Surfaces

Three methods on `BaseHub`, exchanging the pinned contract DTOs as **JSON strings** encoded with
`SyncProtocol.CreateJsonOptions()`:

| Method | Request | Answer |
| --- | --- | --- |
| `sync-register-author-key` | `AuthorKeyRegistrationRequest` | `AuthorKeyRegistrationResult` or `SyncFailure` |
| `sync-send-message` | `SendMessageRequest` | `OperationResult` or `SyncFailure` |
| `sync-query-operation` | `OperationQuery` | `OperationResult` or `SyncFailure` |

A JSON string rather than a hub-serialized object because the v1 encoding (camelCase, exact enum
names, `OperationResult`'s **leading** `outcome` discriminator) must hold regardless of the protocol
the connection negotiated, and docs/sync-contract-v1.md forbids the legacy MessagePack resolver for v1
payloads. It also makes `MaxHubMessageBytes` / `MaxSignedPackageBytes` enforceable on real wire bytes.
A separately routed `/sync/v1` hub is a stage-14 transport concern.

## The author-key registry

`SyncAuthorKeys` is keyed by the complete wire `AuthorKeyReference` (account, chat, key id, key
version). Registration requires a currently trusted device whose **own** account context equals the
reference's (taken from session claims, never the payload) and an **active** stint in that chat; the
SPKI is imported and checked as P-256 before it is stored. The binding is one-way: the identical key
for a bound reference is confirmed (`alreadyRegistered`), a **different** key is `409 Conflict`, and a
rotation must use a new version - which the primary key makes a different row. Old versions are never
removed, so a message stays verifiable forever.

A package is verified **only** against this registry. There is no path by which a key travelling with
its own package becomes what that package is checked against.

## The acceptance transaction

Lock order, extending the established one and taking nothing out of order:

```
TrustedMutation (account -> device -> session)
  -> EnsureProjectedAsync   (projection advisory lock, only for a pre-stage-07 chat)
  -> LockChatAndAuthorizeAsync   (the chat's SyncChatSequenceCounters row, FOR UPDATE)
  -> advisory lock on the operation identity          <- taken last, by nothing else
  -> the operation result lookup
  -> verification
  -> the writes
```

1. **Rights serialize with acceptance.** The chat counter row is the row every membership mutation
   allocates from, so a removal/leave/block either commits before this send (which is then refused) or
   waits behind it.
2. **Sequences are allocated under that lock and held to commit** (`SyncSequenceAllocator`), so no
   reader can observe position N+1 while N is in flight.
3. **The operation-identity advisory lock** serializes two attempts at the same identity even when
   they name different chats (and so lock different counter rows). Nothing else takes it, so it can
   close no cycle.
4. **The result is consulted before any rights check**, which is what makes an authenticated own
   accepted operation recognisable after a membership change.

The transaction writes, in this order: `SyncMessages`, `SyncMessageKeys` (via the key module, never a
transaction of its own), the `SyncChatChanges` entry for the message at the message's own
`ChangeSequence`, `SyncOperationResults`, `SyncOutbox`, then - through `IMessageStatusService` - the
recipients' unread and the sender's OwnSend marks, with one `Unread` / `RecipientState` journal entry
per row actually changed.

## Outcomes

| Situation | Answer | Stored? |
| --- | --- | --- |
| first acceptance | `Accepted` | yes |
| same package, same identity | the **original** `Accepted`, re-read from the message | - |
| different package, same identity | `Rejected(OperationConflict)`; the stored result is untouched | no |
| signature/binding/structure fails against the named key | `Rejected(InvalidPackage)` | **yes** - the same bytes can never verify |
| unsupported version, oversize, foreign device, malformed key | `Rejected(UnsupportedVersion/PayloadTooLarge/Forbidden/InvalidPackage)` | no - deterministic without a row |
| the author key reference is not bound | `Rejected(InvalidPackage)` | no - registering it and retrying the same package works |
| frozen archive, stale scope | `Rejected(MembershipChanged)` | no - rights can legitimately return |
| no stint / excluded context / blocked personal chat | `Rejected(Forbidden)` | no |
| device no longer trusted | `Retryable(SessionExpired)` + connection abort | no |
| nothing committed at the lookup point (`QueryOperation`) | `Unknown` | - |
| the package does not parse at all | `SyncFailure(InvalidRequest)` - no operation can be named | no |

`OwnSendThroughSequence` **is** the accepted message's own `MessageSequence`, so returning the
original result necessarily returns the original boundary; a later incoming message stays unread.

`QueryOperation` performs **no** membership check at all, by contract.

## Unread across two message spaces

A chat can hold legacy `Messages` rows written before this stage and `SyncMessages` rows written after
it. A membership stint still has exactly **one** unread counter; `SyncUnreadStates` keeps a pointer
pair for each space and `MessageStatusService.RecomputeUnreadAsync` counts both, so recomputing after a
Seen in one space cannot erase the other's contribution.

## What this stage does not do

- **Delivery is stage 12.** This transaction only writes the outbox row; claiming, publishing and the
  live fan-out are `docs/outbox-delivery.md`, which also wakes this replica's publisher from the hub
  right after the commit (`AcceptanceOutcome.OutboxId`).
- **No v1 receipts.** `SyncMessageDeviceStates` stays unwritten - the v1 receipt surface arrives with
  the device-side consumer. The legacy `message-receipt` method is unchanged and still operates on
  legacy messages.
- **No v1 history/sync.** The journal entries this stage writes are read by stage 13.
- The legacy `send-message` path is unchanged and still writes legacy messages.
