using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Database.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncSessionsAndCursors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Stage 13 (plan/13-history-api.md). Four additive tables plus three additive columns; no
            // existing column changes and no backfill. Everything a sync depends on after the handshake
            // lives here rather than in a replica's memory, which is what lets the handshake arrive on one
            // API replica and every HTTP page be served by another.

            // The durable initial-restore marker, per local instance: null means every handshake for this
            // SQLite copy is still a Full one, including after a restart. A new copy gets a new row and is
            // therefore Full again even on a known device registration.
            migrationBuilder.AddColumn<DateTime>(
                name: "InitialRestoreCompletedAtUtc",
                table: "SyncLocalInstances",
                type: "timestamp with time zone",
                nullable: true);

            // How far each journal has been cleaned. 0 (the default for every existing row) means nothing
            // was ever pruned, so no reader's behaviour changes until a sweep actually raises it.
            migrationBuilder.AddColumn<long>(
                name: "PrunedThroughChangeSequence",
                table: "SyncChatSequenceCounters",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "PrunedThroughCatalogSequence",
                table: "SyncAccountCatalogCounters",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "SyncSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeviceRegistrationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LocalInstanceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeviceSessionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ConnectionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    RightsVersion = table.Column<long>(type: "bigint", nullable: false),
                    CatalogThroughSequence = table.Column<long>(type: "bigint", nullable: false),
                    SnapshotId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CatalogUploadedSequence = table.Column<long>(type: "bigint", nullable: true),
                    CatalogAcknowledgedSequence = table.Column<long>(type: "bigint", nullable: true),
                    ResumeCompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BoundaryCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncSessions", x => x.Id);
                    table.CheckConstraint("CK_SyncSessions_BoundaryCount_NonNegative", "\"BoundaryCount\" >= 0");
                    // An acknowledgement can never claim more than this session's own fixed catalog bound.
                    table.CheckConstraint("CK_SyncSessions_CatalogAcknowledged_WithinBoundary", "\"CatalogAcknowledgedSequence\" IS NULL OR \"CatalogAcknowledgedSequence\" <= \"CatalogThroughSequence\"");
                    table.CheckConstraint("CK_SyncSessions_CatalogThroughSequence_NonNegative", "\"CatalogThroughSequence\" >= 0");
                    table.CheckConstraint("CK_SyncSessions_ExpiresAfterCreated", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_SyncSessions_RightsVersion_NonNegative", "\"RightsVersion\" >= 0");
                    table.ForeignKey(
                        name: "FK_SyncSessions_AccountId_SyncAccounts_Id",
                        column: x => x.AccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    // Composite: the registration must belong to the same account as the session.
                    table.ForeignKey(
                        name: "FK_SyncSessions_AccountId_DeviceRegistrationId_SyncDeviceRegistrations",
                        columns: x => new { x.AccountId, x.DeviceRegistrationId },
                        principalTable: "SyncDeviceRegistrations",
                        principalColumns: new[] { "AccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    // Composite: the device session must belong to that same account.
                    table.ForeignKey(
                        name: "FK_SyncSessions_AccountId_DeviceSessionId_SyncDeviceSessions",
                        columns: x => new { x.AccountId, x.DeviceSessionId },
                        principalTable: "SyncDeviceSessions",
                        principalColumns: new[] { "AccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    // Composite: the local instance must belong to that same registration.
                    table.ForeignKey(
                        name: "FK_SyncSessions_DeviceRegistrationId_LocalInstanceId_SyncLocalInstances",
                        columns: x => new { x.DeviceRegistrationId, x.LocalInstanceId },
                        principalTable: "SyncLocalInstances",
                        principalColumns: new[] { "DeviceRegistrationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncSessionBoundaries",
                columns: table => new
                {
                    SyncSessionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MembershipId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RightsVersion = table.Column<long>(type: "bigint", nullable: false),
                    ThroughChangeSequence = table.Column<long>(type: "bigint", nullable: false),
                    IsFrozen = table.Column<bool>(type: "boolean", nullable: false),
                    PriorityRank = table.Column<int>(type: "integer", nullable: true),
                    UploadedSequence = table.Column<long>(type: "bigint", nullable: true),
                    AcknowledgedSequence = table.Column<long>(type: "bigint", nullable: true),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    // Ordinal IS the enumeration order the boundary pages and the full catalog walk.
                    table.PrimaryKey("PK_SyncSessionBoundaries", x => new { x.SyncSessionId, x.Ordinal });
                    table.CheckConstraint("CK_SyncSessionBoundaries_AcknowledgedTime", "(\"AcknowledgedSequence\" IS NULL) = (\"AcknowledgedAtUtc\" IS NULL)");
                    table.CheckConstraint("CK_SyncSessionBoundaries_Acknowledged_WithinBoundary", "\"AcknowledgedSequence\" IS NULL OR \"AcknowledgedSequence\" <= \"ThroughChangeSequence\"");
                    table.CheckConstraint("CK_SyncSessionBoundaries_Ordinal_NonNegative", "\"Ordinal\" >= 0");
                    // docs/sync-contract-v1.md fixes the priority set at "up to ten chats".
                    table.CheckConstraint("CK_SyncSessionBoundaries_PriorityRank_Range", "\"PriorityRank\" IS NULL OR (\"PriorityRank\" >= 1 AND \"PriorityRank\" <= 10)");
                    table.CheckConstraint("CK_SyncSessionBoundaries_Through_NonNegative", "\"ThroughChangeSequence\" >= 0");
                    table.ForeignKey(
                        name: "FK_SyncSessionBoundaries_SyncSessionId_SyncSessions_Id",
                        column: x => x.SyncSessionId,
                        principalTable: "SyncSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncSessionBoundaries_ChatId_SyncChats_Id",
                        column: x => x.ChatId,
                        principalTable: "SyncChats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    // Composite: the stint must really belong to this boundary's chat.
                    table.ForeignKey(
                        name: "FK_SyncSessionBoundaries_ChatId_MembershipId_SyncMemberships",
                        columns: x => new { x.ChatId, x.MembershipId },
                        principalTable: "SyncMemberships",
                        principalColumns: new[] { "ChatId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncCursors",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SyncSessionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeviceRegistrationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LocalInstanceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Surface = table.Column<int>(type: "integer", nullable: false),
                    StreamKind = table.Column<int>(type: "integer", nullable: false),
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MembershipId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RightsVersion = table.Column<long>(type: "bigint", nullable: false),
                    SnapshotId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Forward = table.Column<bool>(type: "boolean", nullable: false),
                    FromSequence = table.Column<long>(type: "bigint", nullable: false),
                    UpperBoundSequence = table.Column<long>(type: "bigint", nullable: false),
                    IssuedThroughSequence = table.Column<long>(type: "bigint", nullable: true),
                    NextCursorId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsTerminal = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    // The opaque 256-bit token itself; a cursor is a name for durable state, never an
                    // authority - every page revalidates session, device and current rights first.
                    table.PrimaryKey("PK_SyncCursors", x => x.Id);
                    table.CheckConstraint("CK_SyncCursors_ChatScope_Complete", "(\"ChatId\" IS NULL) = (\"MembershipId\" IS NULL)");
                    table.CheckConstraint("CK_SyncCursors_ExpiresAfterCreated", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_SyncCursors_FromSequence_NonNegative", "\"FromSequence\" >= 0");
                    table.CheckConstraint("CK_SyncCursors_IssuedThrough_WithinBound", "\"IssuedThroughSequence\" IS NULL OR \"IssuedThroughSequence\" <= \"UpperBoundSequence\"");
                    // StreamKind 1 is Chat: the chat stream always has a scope, the catalog never does.
                    table.CheckConstraint("CK_SyncCursors_StreamKind_Scope", "(\"StreamKind\" = 1) = (\"ChatId\" IS NOT NULL)");
                    table.CheckConstraint("CK_SyncCursors_Terminal_HasNoNext", "NOT \"IsTerminal\" OR \"NextCursorId\" IS NULL");
                    table.CheckConstraint("CK_SyncCursors_UpperBound_NonNegative", "\"UpperBoundSequence\" >= 0");
                    table.ForeignKey(
                        name: "FK_SyncCursors_SyncSessionId_SyncSessions_Id",
                        column: x => x.SyncSessionId,
                        principalTable: "SyncSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncPositionExchanges",
                columns: table => new
                {
                    SyncSessionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExchangeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BatchIndex = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ConnectionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RightsVersion = table.Column<long>(type: "bigint", nullable: false),
                    RequestDigest = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: false),
                    Final = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    // The key IS the idempotence rule: one stored answer per (session, exchange, index).
                    table.PrimaryKey("PK_SyncPositionExchanges", x => new { x.SyncSessionId, x.ExchangeId, x.BatchIndex });
                    table.CheckConstraint("CK_SyncPositionExchanges_BatchIndex_NonNegative", "\"BatchIndex\" >= 0");
                    table.ForeignKey(
                        name: "FK_SyncPositionExchanges_SyncSessionId_SyncSessions_Id",
                        column: x => x.SyncSessionId,
                        principalTable: "SyncSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // "Is this session this exact device's, and is it still live" - the lookup every page makes.
            migrationBuilder.CreateIndex(
                name: "IX_SyncSessions_AccountId_LocalInstanceId_CreatedAtUtc",
                table: "SyncSessions",
                columns: new[] { "AccountId", "LocalInstanceId", "CreatedAtUtc" });

            // The repeated-handshake lookup: the session already fixed for this connection.
            migrationBuilder.CreateIndex(
                name: "IX_SyncSessions_DeviceSessionId_ConnectionId",
                table: "SyncSessions",
                columns: new[] { "DeviceSessionId", "ConnectionId" });

            // The three composite foreign keys above need their own indexes; none of the indexes already
            // declared starts with the same column pair.
            migrationBuilder.CreateIndex(
                name: "IX_SyncSessions_AccountId_DeviceRegistrationId",
                table: "SyncSessions",
                columns: new[] { "AccountId", "DeviceRegistrationId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncSessions_AccountId_DeviceSessionId",
                table: "SyncSessions",
                columns: new[] { "AccountId", "DeviceSessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncSessions_DeviceRegistrationId_LocalInstanceId",
                table: "SyncSessions",
                columns: new[] { "DeviceRegistrationId", "LocalInstanceId" });

            // One stint appears at most once in a session's fixed set, which is what lets a submitted
            // position or an acknowledgement name a scope unambiguously.
            migrationBuilder.CreateIndex(
                name: "IX_SyncSessionBoundaries_SyncSessionId_ChatId_MembershipId",
                table: "SyncSessionBoundaries",
                columns: new[] { "SyncSessionId", "ChatId", "MembershipId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncSessionBoundaries_ChatId",
                table: "SyncSessionBoundaries",
                column: "ChatId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncSessionBoundaries_ChatId_MembershipId",
                table: "SyncSessionBoundaries",
                columns: new[] { "ChatId", "MembershipId" });

            // Cursor lifetime follows the sync session, so the sweep that removes an expired session
            // reaches its cursors by exactly this pair.
            migrationBuilder.CreateIndex(
                name: "IX_SyncCursors_SyncSessionId_ExpiresAtUtc",
                table: "SyncCursors",
                columns: new[] { "SyncSessionId", "ExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncPositionExchanges");

            migrationBuilder.DropTable(
                name: "SyncCursors");

            migrationBuilder.DropTable(
                name: "SyncSessionBoundaries");

            migrationBuilder.DropTable(
                name: "SyncSessions");

            migrationBuilder.DropColumn(
                name: "PrunedThroughCatalogSequence",
                table: "SyncAccountCatalogCounters");

            migrationBuilder.DropColumn(
                name: "PrunedThroughChangeSequence",
                table: "SyncChatSequenceCounters");

            migrationBuilder.DropColumn(
                name: "InitialRestoreCompletedAtUtc",
                table: "SyncLocalInstances");
        }
    }
}
