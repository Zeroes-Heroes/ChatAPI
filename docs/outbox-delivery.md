# Outbox, RabbitMQ and live delivery (stage 12)

`plan/12-outbox-delivery.md`. What happens AFTER the acceptance transaction of stage 11 commits: the
outbox row it wrote is claimed under a lease, published to RabbitMQ, consumed by any replica, and
turned into the v1 live event that carries the whole encrypted message, its key grant and the author's
verification key to every device that may have it.

## Who owns what

| Module | Owns |
| --- | --- |
| `Services/Delivery` | the whole post-commit path: the outbox queue, the broker, and building/publishing the live event |
| `Services/Membership` | who may receive it (`DescribeParticipantsAsync` + `ChatAccess.PermitsMessageAt`) - unchanged |
| `Services/Keys` | unwrapping the protected content key for one authorized delivery (first production caller of `UnwrapForAuthorizedDelivery`) |
| `Services/Authorship` | the `AuthorVerificationKey` that travels with every delivery |
| `BaseHub` | the addressing seam only: a connection joins its own device group on connect |

## The path

```
acceptance commit (stage 11)  ->  SyncOutbox row (Pending)
  -> IOutboxPublishSignal.Notify()        (same replica; latency only, never correctness)
  -> OutboxPublisherHostedService          claim under lease -> IDeliveryBroker.PublishAsync
  -> RabbitMQ  chat.delivery (direct, durable) -> chat.delivery.live (durable, persistent messages)
  -> DeliveryConsumerHostedService (any replica) -> ILiveDeliveryService.DeliverAsync
  -> IHubContext<BaseHub>.Clients.Group("sync-device:{registration}")  -> SignalR Redis backplane
  -> every currently trusted device of every account whose rights permit the message
```

The broker payload is identifiers only - `outboxId`, `chatId`, `messageId`, `changeSequence`,
`eventId`. The transient message key may never be written to "SQLite/outbox/broker/logs", and a task
that waited in the queue must not deliver the rights that were true when it was published: the
consumer re-reads the committed message and re-evaluates authorization itself.

## Claiming

`IOutboxStore.ClaimAsync` is one short transaction:

```
SELECT * FROM "SyncOutbox"
WHERE (Status = Pending AND (NextAttemptAtUtc IS NULL OR NextAttemptAtUtc <= now))
   OR (Status = Leased  AND LeasedUntilUtc IS NOT NULL AND LeasedUntilUtc <= now)
ORDER BY "Id" LIMIT n FOR UPDATE SKIP LOCKED
```

then stamps `Status=Leased`, `LeaseOwner`, `LeasedUntilUtc` and `AttemptCount + 1`, and commits -
before anything is published, so no row lock is ever held across a network call. `SKIP LOCKED` is what
makes two replicas polling at the same instant take disjoint rows without waiting for each other.

## States

| Transition | When |
| --- | --- |
| `Pending -> Leased` | claimed; the attempt is counted here, so a publisher that dies silently still consumes one |
| `Leased -> Published` | ONLY after the broker confirmed the publish and the message really routed |
| `Leased -> Pending` (with backoff) | transient failure: `NextAttemptAtUtc = now + min(max, base * 2^(attempt-1))`, with the reason on the row |
| `-> Failed` | attempts exhausted, or the record can never be delivered (no message, no key, no author key): `NextAttemptAtUtc = NULL`, never claimed again, kept for diagnostics and explicit re-release |
| expired lease | treated as claimable again - the crash-recovery case |

The release is ONE statement over the row's own `AttemptCount`, so a publisher and a consumer
releasing the same row concurrently cannot overwrite each other's count.

## Delivery authorization

Before any key material is materialized, for every participant account of the chat:

1. the account's context is current (a superseded fresh-start context is not a participant at all);
2. `ChatAccess.PermitsMessageAt(message.CreatedAtUtc)` - the applicable **historical** rights, so a
   member added later never reaches back before its own window and a removed member keeps exactly its
   archive;
3. the stint that covers the message instant supplies the delivered `MembershipScope`
   (`membershipId` + `rightsVersion`);
4. only device registrations whose trust is `Trusted` **right now** are addressed.

Only then is the content key unwrapped - once per task - and zeroed in a `finally`.

## Addressing

`sync-device:{deviceRegistrationId}`, joined in `BaseHub.OnConnectedAsync` from the session's own
claims. A registration belongs to exactly one account context, so the group is per device AND
fresh-start isolated by construction - unlike the legacy user-id fan-out, which cannot tell a phone's
superseded context from its current one. The SignalR Redis backplane makes the group reachable from
whichever replica consumed the task.

The event name is `sync-change`, and its payload is one `LiveEvent` as a JSON string encoded with
`SyncProtocol.CreateJsonOptions()` - the same v1 encoding the hub's own v1 methods use.

## Repetition is safe

`EventId` is `{chatId}:{changeSequence}` - derived from the chat change, not minted per attempt - so a
republished task (lost confirm, expired lease) and a redelivered one (missing ack) carry the same id,
which is what the contract's "EventId deduplicates delivery" needs. Delivery itself writes nothing, so
a duplicate costs one extra live event and changes no state.

## Later: more than one kind of task

The v1 receipt surface (`docs/sync-receipts.md`) queues outbox rows for STATUS changes too. A row
therefore carries its own `ChangeKind`/`SecondaryId` and `DeliverAsync` switches on them after
re-reading the row by `task.OutboxId` and checking that the task describes it. The broker payload and
everything above are unchanged, and a row written before that change reads as `Message`, which is what
it is. Producing those rows is gated by `Delivery__PublishStatusChanges` (default false) until every
replica runs a consumer that understands the discriminator - an older consumer would deliver a status
row as a full message.

A status task may also be a deliberate no-op: a change produced under a FROZEN stint is answered
`Delivered(0)` and addressed to nobody, because an archive's stream ends at its own fixed bound. The
task is finished and acknowledged exactly like any other - the "no audience" outcome this stage already
had for a message nobody is entitled to.

## What this stage does not do

- **No v1 receipts.** `SyncMessageDeviceStates` stays unwritten: a device reports `Received` only
  after its own durable write and successful verify/decrypt, and that surface is a later stage (now
  `docs/sync-receipts.md`). The server's part - handing over the package, the key and the author key so
  the device CAN verify - is what this stage adds.
- **No push.** The separate push queue, presence suppression and the delayed-task re-check belong to
  the push stage; only the live queue is declared here.
- **No client consumer.** Nothing in the app subscribes to `sync-change` yet: the connection
  lifecycle and the local writer are stages 14/15. A message therefore reaches the device's socket and
  is not yet stored locally.
- **No readiness/drain surface.** Dependency health, rollout and connection redistribution are stage 21.
  With no broker configured, both workers log once and stay idle; accepted messages stay in the outbox.
