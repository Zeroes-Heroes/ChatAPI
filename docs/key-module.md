# Crypto and key module, server side (stage 04)

Stage 04 only. Adds `Services/Crypto/` (compact JWS structural parsing + ES256/P-256/SHA-256
signature verification for the client's signed message package) and `Services/Keys/` (the isolated
server key-protection module: master-secret provider, AES-256-GCM key-wrap, and a provisioning
service that combines them with storage). Does not implement message acceptance, send/receive
workflow, or any hub endpoint (stage 11) - this stage only builds the reusable primitives those later
stages will call, and proves them with tests of their own.

## `Services/Crypto/`: verifying the client's signed package

`CompactJws.Parse` is pure structural parsing (`Base64Url.cs` - a from-scratch canonical-unpadded-
base64url codec mirroring `ChatAppClient/src/Crypto/Base64Url.ts`'s exact rules, independently
implemented): exactly 3 non-empty canonical-base64url segments, a protected header that is *exactly*
`{alg:"ES256", typ:"chat-message+jws", kid}` with no unknown/duplicate fields (`StrictJsonShape.cs` -
a hand-rolled `Utf8JsonReader` walk, because `System.Text.Json`'s normal deserialization silently keeps
only the last occurrence of a duplicate JSON key, and the project-wide `SyncProtocol.CreateJsonOptions()`
deliberately uses `JsonUnmappedMemberHandling.Skip` for additive-outer-field forward compatibility
elsewhere - neither behavior is acceptable for a cryptographically signed package, where an extra or
duplicated field could represent a downgrade/confusion attack), and a signature of exactly 64 bytes.
`CompactJws.Parse` also rejects a compact JWS longer than `SyncProtocol.MaxSignedPackageBytes` before
any decode/parse work at all - a cheap guard at this public parser rather than something every future
hub-layer caller has to remember to check first (mirrors `ChatAppClient/src/Crypto/Jws.ts`'s `parse`).

`MessagePackageVerifier.Verify` takes the caller's own expected `MessageBinding` and the author's SPKI
public key - both must come from the caller's already-authorized state (the current operation identity
and the server-authorized author-key registry), never from the package itself. It verifies the ES256
signature via `ECDsa.VerifyData(..., DSASignatureFormat.IeeeP1363FixedFieldConcatenation)` (.NET
natively supports IEEE P1363 R||S - no manual DER conversion needed, and this exact format/curve
combination is what the real TypeScript client module produces, proven in
`CrossLanguageFixtureTests.cs`), structurally validates the payload with the same `StrictJsonShape`
discipline as the header, checks the payload's own cross-field rule (`authorKey.accountContextId ==
operation.device.accountContextId`, `authorKey.chatId == chatId`), and only then compares every binding
field against the caller's expectation - a mismatch anywhere throws `MessageBindingMismatchException`.
Tampering the ciphertext/nonce/tag is caught here too, but indirectly and correctly: those fields live
inside the signed payload, so any bit-flip breaks the ES256 signature before any content-specific check
would even run (the server never decrypts message content at all - see `SyncMessageEntity`'s doc
comment - so there is nothing further to validate once the signature and binding check out).

One cross-platform gotcha worth recording: an **imported** `ECDsa` key's curve reports
`Oid.FriendlyName == "ECDSA_P256"`, not `"nistP256"` (the name `ECCurve.NamedCurves.nistP256` itself
uses) - comparing `FriendlyName` against `"nistP256"` to confirm "is this a P-256 key" silently rejects
every real imported key. `MessagePackageVerifier` compares the OID *value* (`1.2.840.10045.3.1.7`)
instead, which is stable regardless of which name .NET decides to report.

## `Services/Keys/`: the key-protection module

Four small, focused seams, matching the plan's "clean interfaces for storage/protection/provisioning":

- **`IMasterSecretProvider`** / `FileMasterSecretProvider` - reads master key material only from a
  read-only mounted directory (one `{version}.key` file per version, exactly 32 raw bytes), never from
  PostgreSQL or an application-configuration value carrying the key bytes themselves (only the
  directory path and current-version *name* are configuration). Fails fast at construction - a
  misconfigured or unmounted current version throws `MasterSecretUnavailableException` immediately,
  not on first use - and never includes key bytes in any exception message (`ExceptionMessages_
  NeverContainKeyBytes` test). Old master versions remain readable (`GetKey(oldVersion)`) as long as
  their file stays mounted, independent of which version `CurrentVersion` now points at - this is what
  makes rewrap possible without a flag day. `GetKey` returns a fresh defensive copy every call, never
  the same backing array twice (`GetKey_ReturnsADistinctArrayInstance_EveryCall...` test) - the cache
  holds one array per version for the process lifetime, and without a clone a caller zeroing "its own"
  returned key (a reasonable-looking hygiene habit) would permanently corrupt the cached master key for
  every subsequent Protect/Unprotect call, server-wide, until restart. `AesGcmMessageKeyProtector` zeroes
  its own copy in a `finally` once the AES-GCM call is done, never the shared cache and never a value
  it returns to its own caller.
- **`IMessageKeyProtector`** / `AesGcmMessageKeyProtector` - AES-256-GCM wrap/unwrap using
  `System.Security.Cryptography.AesGcm`, fresh random 12-byte nonce per protect call, associated data
  built by `KeyProtectionAad` as a versioned compact JSON string array (same style as the wire
  contract's own AADs): `["chat-server-key-v1","1",formatVersion,masterKeyVersion,purpose,
  ownerContextId,keyId]`. Binding the master key version *into* the AAD (not just storing it alongside)
  means a stored row can only ever be unprotected under the exact version it says it used - substituting
  any of format version, purpose, owner context (chat id), key id (message id) or master version fails
  AES-GCM authentication (`KeyProtectionIntegrityException`), proven per-field in
  `AesGcmMessageKeyProtectorTests.Unprotect_RejectsContextSubstitution`.
- **`IProtectedMessageKeyStore`** - deliberately thin: `Stage(db, entity)` is exactly `db.
  SyncMessageKeys.Add(entity)`, nothing else. No `SaveChangesAsync`, no transaction, no connection
  management - the entire point of this seam is that the *caller* (a future message-acceptance
  workflow) owns the `AppDbContext`/transaction and adds the message, this row, the operation result
  and everything else to it together, then calls `SaveChangesAsync()`/commits once. Existing as its own
  interface (rather than callers touching `db.SyncMessageKeys` directly) is what lets a future stage
  swap the storage backend without touching protection logic.
- **`IMessageKeyProvisioningService`** - the only entry point that combines the two: `ProtectForStorage`
  builds (but does not store) a `SyncMessageKeyEntity`; `UnwrapForAuthorizedDelivery` and `Rewrap`
  require the caller to assert the exact `(messageId, chatId)` it has already authorized
  (`MessageKeyAccessContext`) and check it against the stored row *before* attempting any
  cryptography (`UnwrapForAuthorizedDelivery_DoesNotEvenAttemptCryptoWhenContextMismatches` proves the
  order) - throwing `KeyContextMismatchException` on any mismatch. This is what keeps the module from
  being an open "decrypt by id" endpoint: nothing in this module ever looks up a key by id alone.
  `Rewrap` unwraps under the row's own `KeyVersion`, re-protects under `IMasterSecretProvider.
  CurrentVersion`, and mutates the same entity's `KeyVersion`/`Nonce`/`ProtectedKey`/`Tag` in place -
  never `MessageId`/`ChatId`, and never the underlying raw key - matching "Rotation never changes
  message ciphertext, author package, MessageId or rights version."

## Storage shape: `SyncMessageKeyEntity` extended, not replaced

Stage 02 already created `SyncMessageKeys` (`MessageId`, `KeyVersion`, `ProtectedKey`, `Nonce`, `Tag`,
`CreatedAtUtc`) as a storage-only placeholder, explicitly deferring "the exact AAD encoding and
migration version" to this stage (see its own doc comment and `docs/sync-contract-v1.md`'s "Server
protected-key records ... This is an internal key-module storage format ... must be specified and
tested in the key-module plan before storage is activated"). This stage adds `ChatId` (the AAD "owner
context" - message-key access is governed by chat membership/rights, so the chat is the natural
authorization scope anchor), `FormatVersion` and `Purpose` (both required, both part of the AAD) via a
new, purely additive EF Core migration (`AddMessageKeyAadFields` - `dotnet-ef migrations
has-pending-model-changes` confirms no further drift). `ChatId` is bound by a **composite** FK
`(ChatId, MessageId) -> SyncMessages(ChatId, Id)`, anchored on `SyncMessages`' own
`AK_SyncMessages_ChatId_Id` alternate key - not an independent single-column FK straight to `SyncChats`
(the first version of this migration had one; review-report.md HIGH #5 flagged that nothing tied the
two FKs together, so a row could claim a `ChatId` that disagreed with its own message's real chat,
silently corrupting the AAD's authorization scope). This mirrors the same codebase's own
`SenderMembership` composite-FK pattern (`SyncMessageConfiguration.cs`) and is enforced at the database
level, proven against a populated table (`SchemaRejects_AProtectedKeyRowClaimingAChatIdThatDisagreesWith
ItsMessagesRealChat`, and a direct-SQL scratch check against a real row pair). Deleting the owning
message still cascades to its key row (now via the composite FK's own `Cascade`, superseding the
original single-column `MessageId` FK the migration also drops). The migration also fails loudly and
explicitly (a `RAISE EXCEPTION` precondition, not an incidental FK error) if `SyncMessageKeys` is ever
non-empty when it runs - it has no backfill/rewrap path for a pre-plan04 row format, because none has
ever existed. This is additive at the schema level in the same sense stage 02's own migrations are:
`SyncMessageKeys` has never been activated/deployed, so altering its shape now (before any real row
exists) does not touch deployed data - it is exactly the planned-for "specify before activation" step,
not an exception to the "no `AlterColumn`/`DropColumn` on a live table" rule stage 02 documents for
tables that *are* already live.

`MessageId` already serves as the row's key id (1:1 with the message, same PK), and `KeyVersion`
already meant "master key version, independent of RightsVersion" per its original doc comment - neither
needed renaming to fit the generic `KeyProtectionContext` the protection/provisioning layer uses
internally (`OwnerContextId` -> `ChatId`, `KeyId` -> `MessageId`).

## Transaction integration (proven against real PostgreSQL)

`KeyModuleTests` (a new project, parallel to `ServerDataTests` - a separate `DatabaseFixture`/
`TestData` copy so the inherited stage-02 test project stays untouched) runs against a real,
disposable PostgreSQL 17 and proves:

- `Commit_PersistsMessageAndProtectedKeyTogether_InOneSaveChanges` / `ExplicitTransaction_
  CommitPersistsBothMessageAndKey` - one `AppDbContext`, one `SaveChangesAsync`/commit, no separate
  hidden save inside the key module (verified by construction: `ProtectedMessageKeyStore.Stage` has no
  `SaveChanges` call to even hide).
- `ExplicitTransaction_RollbackLeavesNeitherMessageNorKeyPersisted` - staging both rows then rolling
  back leaves neither persisted, proving they share one atomic unit rather than two independent writes.
- `DeletingTheMessageCascadesToItsProtectedKeyRow` - the `Cascade`-delete behavior still holds under
  the composite `(ChatId, MessageId)` FK, not just the original single-column `MessageId` FK it
  supersedes.
- `DbDumpAlone_WithoutTheMasterSecret_CannotRecoverTheRawKey` - reads the stored row back through a
  fresh connection (simulating "just the database"), confirms the stored ciphertext never equals the
  raw key, and shows that even constructing a protector over a *plausible but wrong* 32-byte secret
  (an attacker's own guess, not the real mounted one) fails AES-GCM authentication rather than ever
  reconstructing the real key - "A DB dump alone must not unlock keys."

## What is explicitly out of scope here

- Message acceptance / the SendMessage hub workflow that would actually call
  `IMessageKeyProvisioningService` in production (stage 11).
- Recovery (`Argon2id`-derived recovery key protection) and device enrollment/approval (stages 05/06).
- Wiring `IMasterSecretProvider`/`IMessageKeyProtector`/etc. into DI (`ServiceCollectionExtension.cs`)
  - nothing in a later-stage workflow calls these yet, so registering them now would be dead wiring;
  tests construct them directly.
