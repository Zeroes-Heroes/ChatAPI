# Recovery and device approval, server side (stage 06)

Stage 06 only (plan/06-recovery.md). Adds `Services/Recovery/` (recovery envelope storage + the
bootstrap recovery route), extends `Services/Sessions/` with approval grants, author-key handoff and the
fresh-start boundary, and adds `Turbo/Controllers/Recovery/`. Message acceptance, sync and membership
remain out of scope.

## Two ways back in, one trust decision

`MESSAGE_SYNC_ANALYSIS.md:153` offers the recovery secret **or** an approval from another trusted device.
Both end at the same owner of the rule, `DeviceTrustService.TryTrustPendingDeviceAsync`:

| account state | approval | recovery proof |
| --- | --- | --- |
| no trusted device, no revocation | not needed (first device) | not needed |
| trusted device exists, no revocation | sufficient | sufficient |
| sticky barrier active (after any explicit revocation) | **required** | never sufficient |

`IsRecoverySufficientAsync` is that same rule, exposed for one earlier enforcement point: the sealed
envelope is **not released at all** to a device the barrier blocks (the plaintext is the account's author
private keys, so refusing only the session afterwards would already have handed them over). The decision
is then re-made under the commit locks for revocation races.

## Recovery envelope

`SyncRecoveryEnvelopes` keeps one row per (account context, recovery version):

- the client envelope (its own nonce/ciphertext/tag, sealed on-device under the Argon2id-derived key with
  the pinned `chat-recovery-v1` AAD) is wrapped a second time under the mounted master secret
  (`IMessageKeyProtector`, purpose `recovery-envelope`), so a PostgreSQL dump alone cannot even start an
  offline guess;
- the KDF metadata is stored in the exact canonical form the AAD is built from, and
  `RecoveryKdfMetadata.FromWire` enforces the pinned algorithm/salt/bounds (`SyncProtocol.RecoveryKdf*`,
  shared with the client) before anything is stored. Tampering with it fails the client's own open,
  because the AAD covers it;
- `VerificationSpki` is public key material held in clear: the private half lives inside the envelope, so
  a `chat-recovery-proof-v1` signature over the server's own challenge is proof that the secret was
  actually used - the server never sees the secret or the derived key.

`StoreAsync` takes account → device → session locks and re-runs the shared `ISessionTrustChecker` rule
under them, so authority granted at HTTP entry cannot be used after a revocation or fresh start commits.
Versions are monotonic and never renumbered server-side (the version is inside the client's AAD).

**One recovery password per account context.** The system password manager holds a single entry per
(domain, phone), and the server cannot observe what it ended up holding, so rotation would be a guess:

- every stored version stays a recovery candidate forever — nothing is ever deleted, because any attempt
  whose confirmation was lost may be exactly the password that was saved;
- `ConfirmAsync` only records the save (idempotent for the same version, `RecoveryAlreadySetUpException`
  for a different one); it retires nothing;
- once a version is confirmed, `StoreAsync` refuses further setups with `RecoveryAlreadySetUpException`
  (HTTP 409) and the client hides the setup entry point;
- unconfirmed attempts are capped (`MaxStoredVersions = 5`): the cap refuses the *next* upload rather than
  dropping an older row that might still be the saved secret.

### Author-key escrow (the update path a frozen envelope cannot have)

The envelope's plaintext can only be re-sealed by someone holding the secret, and the secret lives in
the user's password manager - so an account whose recovery was set up last month would otherwise
recover none of the authorship created since. `SyncAuthorKeyEscrows` closes that gap:

- one append-only row per (account context, recovery version, author key), sealed on-device to the
  version's PUBLIC `VerificationSpki` (ephemeral ECDH P-256 + HKDF-SHA256 + AES-256-GCM, AAD
  `chat-authorkey-escrow-v1` binding account/version/author key/chat/author key version) and wrapped
  again at rest under the master secret (purpose `authorkey-escrow`);
- every candidate publishes its own key (`RecoveryPackage.VerificationKeySpki`), because a device must
  escrow to **every accepted version**, not only the confirmed one: an upload whose confirmation was
  lost may be the password the system manager actually kept, and the recovery routes accept it. Sealing
  only to the confirmed version would silently lose post-setup authorship for exactly that user; the
  fan-out stays bounded by `MaxStoredVersions`;
- sealing needs no secret, so any trusted device keeps the set complete; opening needs the recovery
  key from inside the envelope, so the server still cannot read a single author key;
- `StoreAuthorKeyEscrowsAsync` takes the same account -> device -> session locks and the same
  commit-time trust rule as the envelope writes, validates every entry before the transaction, and is
  idempotent per author key (a retry after a lost response rewrites nothing);
- `GetRecoveryCandidatesAsync`'s callers hand the entries to the recovering device alongside the
  envelope (`RecoveryBootstrapChallenge.Escrows`, `GET /recovery/author-keys`).

## Approval, bound to the exact request

The approving and requesting devices have no channel between them, so the server carries the approval:
`SyncDeviceApprovalGrants` (PK = the challenge id). The signature itself binds challenge, purpose,
account, requesting device, its key id and SPKI, nonce and expiry; the requesting **local instance** is
bound through the challenge row (`SyncDeviceChallenges.LocalInstanceId`, new in this stage) because the
pinned v1 array has no field for it. Registration re-compares the grant against the challenge it actually
consumed, re-verifies the signature against the approver's *current* key, and claims the grant with a
single conditional `UPDATE` (replay-safe). The approver's device trust **and** that exact session's
liveness are re-read under row locks inside the commit - `ApprovingSession` from the request body is
never authority.

`SyncAuthorKeyHandoffs` carries the author keys the approver sealed to the requesting device's own public
key (ephemeral ECDH P-256 + HKDF-SHA256 + AES-256-GCM, AAD binding the exact pending request), so
approval alone restores the original authorship identity without the recovery secret. Delivery is
**repeatable until acknowledged**: `GET /recovery/handoff` does not consume anything, and
`POST /recovery/handoff/ack` marks it delivered only after the device has imported the keys locally, so a
lost response or a crash cannot destroy the only copy.

## Fresh start and the legacy boundary

`StartNewAccountContextAsync` ends the current context and creates a new `AccountContextId`
(`PreviousAccountId` recorded). Before ending it, it takes the old context's device and session locks in
the module's account → device → session order, so an in-flight legacy write (which holds exactly those
rows through `TrustedMutation`) either commits first - and is then captured by the exclusion snapshot -
or blocks and fails its own commit-time trust check against the now-ended context.

The legacy chat/message tables have no account context and no creation timestamp, so the boundary is
recorded as data: `SyncLegacyContextExclusions` lists the legacy chats that existed for that phone at the
moment of the fresh start. `IAccountContextBoundary` applies it at the surfaces that actually serve them:
`ChatService.GetChats`, `BaseHub`'s connect-time undelivered scan, `SendMessage`, `ChatEntered`, and -
because SignalR addresses users by legacy id - recipient-side delivery (`ResolveLegacyRecipientsAsync`
switches an affected phone to per-connection targeting). Push is the same problem without a connection
to target: a device token is registered per legacy user id only, so `NotifiableLegacyUserIdsAsync` drops
any phone whose CURRENT context excludes the chat before `NotificationForNewMessage` is dispatched (the
payload is the message text plus the old chat id). An ended context has no live session left either way,
so nothing legitimate is lost. `NotificationForNewChat` needs no filtering - a newly created chat is
never excluded. For every account that never started over, all of this is empty and behaviour is
unchanged.

`POST /recovery/new-context` is **idempotent per the caller's own operation id**. The device mints and
durably stores that id before it sends the request (`Services/Recovery/PendingFreshStart.ts`), and the
successor records it (`SyncAccounts.FreshStartOperationId`, unique per LegacyUserId). A retry after a
lost response or a process death therefore resolves - under the same account row lock - to the context
that operation already created, instead of ending it and chaining another destructive restart; it works
even once the successor has devices and data of its own. An operation whose own context has since been
superseded is **not** resurrected and not disclosed (409): the caller must start a new operation, which
is what an explicit later "start over" is. The lookup is scoped to the authenticated LegacyUserId, so
another identity's operation id names nothing here.

A device enrolls into the successor context as a **new registration of a new local copy**:
`SyncDeviceRegistrations` and `SyncLocalInstances` are each keyed by their id alone, so a
DeviceRegistrationId belongs to exactly one account context and a LocalInstanceId to exactly one
registration. The ids an install used in the context it left can never be registered again, which is why
`DeviceChallengeService`/`LocalInstanceRegistrar` refuse them explicitly instead of letting the write
fail as a key violation, and why the client mints both anew when it starts over
(`Crypto/DeviceKeyContext.startNewDeviceRegistration`, `LocalInstanceIdentity.startNewLocalInstanceId`).
The old rows stay with the old context, trust state and all - nothing is rebound, so this is not a way to
leave a revocation barrier behind: the new registration exists only in the new, empty context. For the
same reason a challenge issued before the fresh start no longer registers at all
(`DeviceEnrollmentCoordinator` checks `EndedAtUtc`, as the recovery coordinator already did).

That refusal distinguishes **one exact case**: when the id's owner is an ENDED context of the same
verified identity (`AccountContextOwnership`), the answer is `DeviceIdentitySupersededException` → **409**,
because a sibling install that was not part of the fresh start would otherwise have no way back in. Every
other foreign owner - another phone's context, live or ended - stays the uninformative 401, so the 409
discloses nothing the caller did not authenticate as. It grants nothing either: the client's response is
to mint new ids and enroll into the CURRENT context, where that context's own first-device/approval/
barrier rules decide, and no second fresh start is performed (the active successor is never ended by it).

Known limit, stated plainly: the connection registry is per-replica (plan 21 territory), so for a phone
that has started over, a connection parked on another replica misses the live event instead of receiving
the old context's content - the fail-closed side of that limitation.

## Authorization

Both recovery-relevant controllers declare authorization **per action**, with no class-level
`[Authorize]`: ASP.NET Core ANDs controller and action policies, and this project's app-session and
bootstrap requirements demand mutually exclusive token purposes, so a class-level default policy made
every bootstrap endpoint unreachable. This also fixed the inherited `DeviceSessionController`.
`AllowAnonymous` is not used to work around it anywhere.

| endpoint | policy |
| --- | --- |
| `GET/POST /recovery/envelope`, `GET /recovery/approval-requests`, `POST /recovery/approvals`, `GET /recovery/handoff`, `POST /recovery/handoff/ack` | default (currently trusted app session) |
| `POST /recovery/bootstrap-challenge`, `POST /recovery/bootstrap-register`, `POST /recovery/new-context` | `Bootstrap` (legacy SMS/PIN credential) |

## Tests and what is not proven here

`tests/RecoveryTests/` (envelope storage/validation/commit-time authority, the recovery-proof route and
the barrier matrix, approval binding/expiry/replay/revoked approver/concurrency, handoff retry+ack,
fresh-start isolation and recipient boundary) and `tests/CryptoTests/CrossLanguageRecoveryFixtureTests.cs`
(AAD parity and AES-GCM interop against the real client-produced fixture) are **authored but not
executed**: this stage prohibits builds, so no `dotnet build/test` was run. Schema/DDL behaviour was
verified instead by applying the migration's own DDL to a disposable PostgreSQL and exercising the
constraints with direct SQL. Native Argon2id timing on real phones remains an on-device check.
