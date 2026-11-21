# Server data migrations (stage 02)

Schema migrations are a separate, explicitly invoked step, not something that runs implicitly on
every API startup. This document covers the deployment step itself, the additive migration
convention the sync tables follow, how to stand up an isolated PostgreSQL for `tests/ServerDataTests`,
and the sequence/snapshot identity conventions the schema relies on.

## Why migrations are a separate step

Normal `dotnet Turbo.dll` / `dotnet run` startup (`Turbo/Program.cs`, `Main`) never calls
`AppDbContext.ApplyMigrations()`. If it did, every replica of a multi-replica deployment would race
to mutate the schema concurrently on startup, and a new version's migrations could partially apply
before being verified. Instead:

- `dotnet Turbo.dll --migrate-only` is a dedicated CLI mode (checked first in `Main`) that builds a
  minimal host with **only** configuration and the database connection (`AddDatabase`) - Redis,
  Twilio, Firebase, SignalR and JWT are never initialized for this path - then calls
  `AppDbContext.ApplyMigrations()` and exits.
- Run it **once**, before the first replica of a new version starts serving traffic (e.g. as a
  Kubernetes Job/initContainer, a CI/CD deploy step, or manually for docker-compose).

### Config/connection semantics

`--migrate-only` reads the exact same configuration keys as normal startup
(`Services/Extensions/ServiceCollectionExtension.cs`, `AddDatabase`): `Host`, `Port`, `Username`,
`Password`, `Database`, `IncludeErrorDetails`, sourced from environment variables (and `../.env` via
`DotNetEnv` when not running under `DOTNET_RUNNING_IN_DOCKER_COMPOSE`) - the same `.env` file
`docker-compose.yml`'s `chatserver.api` service already uses via `env_file: .env`. There is no
separate connection-string format for the migration step; it is the application's own database
connection, just without the rest of the application wired up.

### docker-compose example

`docker-compose.yml` defines `chatserver.api` built from `./Turbo/Dockerfile` with `env_file: .env`
(unchanged by this stage - no compose file edits were made, to keep this stage's footprint minimal).
Since compose can no longer rely on implicit auto-migration, run the migration step once before
bringing the API up:

```sh
# One-time (or per-deploy) schema migration step, using the same .env docker-compose already loads:
docker compose run --rm chatserver.api dotnet Turbo.dll --migrate-only

# Then start the API normally:
docker compose up -d chatserver.api redis
```

`docker compose run --rm` reuses the `chatserver.api` image/env/volumes without publishing its ports
or starting the long-running process, so it is safe to run repeatedly (applying migrations is
idempotent - already-applied migrations are skipped).

## Rolling, additive schema order

Every stage-02 table is new (`Sync*`); no existing legacy table (`Users`, `Chats`, `Messages`,
`MessagesStatus`, `UserDevices`, ...) is altered, and no existing column is added, dropped or
renamed. Verify this for any future migration on this model with:

```sh
dotnet-ef migrations has-pending-model-changes --project Database/Database.csproj --startup-project Turbo/Turbo.csproj
```

and by inspecting the generated migration for anything other than `CreateTable` (an `AlterColumn`,
`DropColumn` or `RenameColumn` touching a pre-existing table is a sign the change is not additive
and needs a different approach - e.g. add-then-backfill-then-switch across two deploys instead of a
single in-place alter). This additive discipline is what makes "two simultaneously running versions"
safe during a rolling deploy: an old-version replica that has never heard of the new `Sync*` tables
simply never queries them, and a new-version replica finds them already created by the
`--migrate-only` step that ran before it started.

### Rollback caveats

`dotnet-ef database update <previous-migration>` rolls back cleanly in this stage (verified: all 20
`Sync*` tables drop, 0 legacy tables/columns touched) **only while no replica has written real data
into the new tables yet**. Once any `SyncMessages`/`SyncOperationResults`/etc. row exists, rolling
back drops that data permanently (the `Down()` migration is a straight `DropTable`, not an
archive/export step). Only roll back before the first `--migrate-only` + deploy of a version that
actually writes to these tables, or after confirming the data loss is acceptable (e.g. a disposable
test database).

## Isolated PostgreSQL for `tests/ServerDataTests`

`tests/ServerDataTests` runs its full migration chain against a real PostgreSQL and must never point
at a shared/production database - tests create, mutate and drop real rows. `DatabaseFixture` reads
connection coordinates from:

| Env var | Default |
| --- | --- |
| `SERVERDATATESTS_PG_HOST` | `127.0.0.1` |
| `SERVERDATATESTS_PG_PORT` | `55432` |
| `SERVERDATATESTS_PG_USER` | `postgres` |
| `SERVERDATATESTS_PG_DB` | `chatapp_plan02_test` |

Point these at any disposable PostgreSQL 17 instance (a locally extracted binary, a throwaway
container, etc. - not the application's real database). Example:

```sh
SERVERDATATESTS_PG_HOST=127.0.0.1 SERVERDATATESTS_PG_PORT=55432 \
SERVERDATATESTS_PG_USER=postgres SERVERDATATESTS_PG_DB=chatapp_plan02_test \
  dotnet test tests/ServerDataTests/ServerDataTests.csproj
```

The fixture calls `Database.Migrate()` once per test run, so the target database only needs to exist
and be reachable with `trust`/matching credentials - the fixture applies the full migration chain
itself. Drop and recreate the database between unrelated test sessions if you want a clean slate;
nothing about the tests requires state to persist across runs.

## Sequence and snapshot identity conventions

These conventions are enforced at the database level (CHECK constraints, filtered unique indexes,
composite foreign keys), not only in application code, so they hold even if a future stage's service
layer gets them wrong:

- **0 is reserved for "nothing applied/sent in this stream yet"; the first real item is always 1.**
  `SyncChatSequenceCounterEntity.NextMessageSequence`/`NextChangeSequence` and
  `SyncAccountCatalogCounterEntity.NextCatalogSequence` seed at 1
  (`Database.Sequencing.SyncSequenceConventions.FirstSequence`), and every persisted sequence column
  (`SyncMessages.MessageSequence`/`ChangeSequence`, `SyncChatChanges.ChangeSequence`,
  `SyncCatalogEntries.CatalogSequence`, `SyncMembershipHistoryGrants.FromMessageSequence`/
  `ThroughMessageSequence`/`FrozenAtChangeSequence`) has a `> 0` CHECK constraint. This keeps the
  wire contract's "0 items applied" (a fresh Resume position for a newly authorized stream) and
  "item #1 applied" permanently distinguishable, which an inclusive "position reached" cursor
  compared with `> reportedPosition` depends on to avoid skipping the first committed item.
- **`AcceptedMessage.OwnSendThroughSequence` is `MessageSequence - 1`**, computed by
  `Database.Sequencing.SyncSequenceConventions.OwnSendThroughSequence` (see its doc comment and
  `tests/ServerDataTests/SequenceConventionsTests.cs`). For the first message ever sent in a chat
  (`MessageSequence == 1`) this correctly yields `0` - the same empty-stream sentinel, meaning "no
  older message exists to mark", not a value invented for that case. Because it depends only on the
  message's own already-immutable `MessageSequence`, retrying an already-accepted operation (which
  must return the original persisted result, never recompute it) always observes the same boundary.
- **`SyncAccountEntity.LegacyUserId` is unique only among currently-active contexts.** `EndedAtUtc`
  (nullable; `NULL` = current) is the lifecycle marker; the unique index
  (`IX_SyncAccounts_LegacyUserId_Active`) is filtered to `WHERE "EndedAtUtc" IS NULL`, and a CHECK
  constraint requires `EndedAtUtc >= CreatedAtUtc` when set. This allows a fresh account context for
  the same legacy phone/user while the old, blocked context and all its data are retained (never
  deleted) - see `RelationalIntegrityTests.Account_FreshContextAlongsideRetainedEndedContext_*` and
  `Account_WithTwoCurrentContexts_ForSameLegacyUser_IsRejected`. Setting `EndedAtUtc` itself is a
  later-stage (recovery/auth) responsibility; this stage only provides the schema shape.
- **`HistoryRange.StateSnapshotId` has no dedicated storage column.** `SyncMembershipHistoryGrantEntity`
  does not store a `StateSnapshotId`: for a frozen range (`FrozenAtChangeSequence` set), the
  snapshot identity on the wire IS the grant's own `MembershipId` - the exact id
  `SyncMembershipMetadataSnapshotEntity` and `SyncMembershipMessageStatusSnapshotEntity` are already
  keyed and foreign-keyed by, so the service layer that serializes `HistoryRange` for a frozen range
  should emit `StateSnapshotId = MembershipId` rather than a second, separately-stored and
  unconstrained identity for the same thing. A still-open (not yet frozen) range has no frozen
  snapshot to name. No "live fixed view" snapshot mechanism exists anywhere else in this schema yet,
  so this stage deliberately does not add a column that would claim to provide one; a later stage
  that introduces a live-view snapshot concept should define and persist that identity explicitly at
  that time, rather than this stage inventing a placeholder for it now.
