using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Database.Migrations
{
    /// <inheritdoc />
    public partial class AddRecoveryAndApprovalModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SyncDeviceChallenges has never been written to by any shipped code (stage 05 added the
            // table, which is not deployed). The new LocalInstanceId column is NOT NULL for every
            // enrollment/approval purpose, so fail loudly here instead of letting a pre-existing row
            // collide with the check constraint below - same precondition style as
            // AddDeviceSessionsModel/AddMessageKeyAadFields.
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM ""SyncDeviceChallenges"") THEN
                        RAISE EXCEPTION 'AddRecoveryAndApprovalModel requires an empty SyncDeviceChallenges table; no pre-plan06 challenge row carries a local-instance binding to backfill.';
                    END IF;
                END $$;
            ");

            migrationBuilder.AddColumn<string>(
                name: "PreviousAccountId",
                table: "SyncAccounts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncAccounts_PreviousAccountId",
                table: "SyncAccounts",
                column: "PreviousAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncAccounts_PreviousAccountId_SyncAccounts_Id",
                table: "SyncAccounts",
                column: "PreviousAccountId",
                principalTable: "SyncAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // The client-minted fresh-start operation that created a context, unique per identity:
            // what makes POST /recovery/new-context resolve a retry to the context the first attempt
            // already created instead of ending it and chaining another.
            migrationBuilder.AddColumn<string>(
                name: "FreshStartOperationId",
                table: "SyncAccounts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncAccounts_LegacyUserId_FreshStartOperation",
                table: "SyncAccounts",
                columns: new[] { "LegacyUserId", "FreshStartOperationId" },
                unique: true,
                filter: "\"FreshStartOperationId\" IS NOT NULL");

            migrationBuilder.AddColumn<string>(
                name: "LocalInstanceId",
                table: "SyncDeviceChallenges",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SyncDeviceChallenges_LocalInstanceMatchesPurpose",
                table: "SyncDeviceChallenges",
                sql: "(\"Purpose\" = 2 AND \"LocalInstanceId\" IS NULL) OR (\"Purpose\" != 2 AND \"LocalInstanceId\" IS NOT NULL)");

            migrationBuilder.CreateTable(
                name: "SyncRecoveryEnvelopes",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RecoveryVersion = table.Column<long>(type: "bigint", nullable: false),
                    FormatVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    KdfAlgorithm = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    KdfVersion = table.Column<int>(type: "integer", nullable: false),
                    Salt = table.Column<byte[]>(type: "bytea", nullable: false),
                    MemoryKiB = table.Column<int>(type: "integer", nullable: false),
                    Iterations = table.Column<int>(type: "integer", nullable: false),
                    Parallelism = table.Column<int>(type: "integer", nullable: false),
                    MasterKeyVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    ProtectedEnvelope = table.Column<byte[]>(type: "bytea", nullable: false),
                    Tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    VerificationSpki = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedByDeviceRegistrationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRecoveryEnvelopes", x => new { x.AccountId, x.RecoveryVersion });
                    table.CheckConstraint("CK_SyncRecoveryEnvelopes_ConfirmedNotBeforeCreated", "\"ConfirmedAtUtc\" IS NULL OR \"ConfirmedAtUtc\" >= \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_SyncRecoveryEnvelopes_KdfVersion", "\"KdfVersion\" = 19");
                    table.CheckConstraint("CK_SyncRecoveryEnvelopes_NonceLength", "octet_length(\"Nonce\") = 12");
                    table.CheckConstraint("CK_SyncRecoveryEnvelopes_RecoveryVersionPositive", "\"RecoveryVersion\" >= 1");
                    table.CheckConstraint("CK_SyncRecoveryEnvelopes_ResourceBounds", "\"MemoryKiB\" BETWEEN 8192 AND 1048576 AND \"Iterations\" BETWEEN 1 AND 10 AND \"Parallelism\" BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_SyncRecoveryEnvelopes_SaltLength", "octet_length(\"Salt\") = 16");
                    table.CheckConstraint("CK_SyncRecoveryEnvelopes_TagLength", "octet_length(\"Tag\") = 16");
                    table.ForeignKey(
                        name: "FK_SyncRecoveryEnvelopes_AccountId_SyncAccounts_Id",
                        column: x => x.AccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncRecoveryEnvelopes_AccountId_CreatedBy_SyncDeviceRegistrations",
                        columns: x => new { x.AccountId, x.CreatedByDeviceRegistrationId },
                        principalTable: "SyncDeviceRegistrations",
                        principalColumns: new[] { "AccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncRecoveryEnvelopes_AccountId_CreatedByDeviceRegistrationId",
                table: "SyncRecoveryEnvelopes",
                columns: new[] { "AccountId", "CreatedByDeviceRegistrationId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncRecoveryEnvelopes_AccountId_ConfirmedAtUtc",
                table: "SyncRecoveryEnvelopes",
                columns: new[] { "AccountId", "ConfirmedAtUtc" });

            migrationBuilder.CreateTable(
                name: "SyncDeviceApprovalGrants",
                columns: table => new
                {
                    ChallengeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PendingDeviceRegistrationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PendingKeyId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PendingPublicKeySpki = table.Column<byte[]>(type: "bytea", nullable: false),
                    PendingLocalInstanceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ApprovingDeviceRegistrationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ApprovingSessionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncDeviceApprovalGrants", x => x.ChallengeId);
                    table.CheckConstraint("CK_SyncDeviceApprovalGrants_ApproverIsNotRequester", "\"ApprovingDeviceRegistrationId\" <> \"PendingDeviceRegistrationId\"");
                    table.CheckConstraint("CK_SyncDeviceApprovalGrants_ConsumedNotBeforeCreated", "\"ConsumedAtUtc\" IS NULL OR \"ConsumedAtUtc\" >= \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_SyncDeviceApprovalGrants_ExpiresAfterCreated", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_SyncDeviceApprovalGrants_SignatureLength", "octet_length(\"Signature\") = 64");
                    table.ForeignKey(
                        name: "FK_SyncDeviceApprovalGrants_AccountId_SyncAccounts_Id",
                        column: x => x.AccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncDeviceApprovalGrants_ChallengeId_SyncDeviceChallenges_Id",
                        column: x => x.ChallengeId,
                        principalTable: "SyncDeviceChallenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncDeviceApprovalGrants_AccountId_Approving_SyncDeviceRegistrations",
                        columns: x => new { x.AccountId, x.ApprovingDeviceRegistrationId },
                        principalTable: "SyncDeviceRegistrations",
                        principalColumns: new[] { "AccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncDeviceApprovalGrants_AccountId_Pending_SyncDeviceRegistrations",
                        columns: x => new { x.AccountId, x.PendingDeviceRegistrationId },
                        principalTable: "SyncDeviceRegistrations",
                        principalColumns: new[] { "AccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncDeviceApprovalGrants_AccountId_ApprovingDeviceRegistrationId",
                table: "SyncDeviceApprovalGrants",
                columns: new[] { "AccountId", "ApprovingDeviceRegistrationId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncDeviceApprovalGrants_AccountId_PendingDeviceRegistrationId",
                table: "SyncDeviceApprovalGrants",
                columns: new[] { "AccountId", "PendingDeviceRegistrationId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncDeviceApprovalGrants_ChallengeId_ConsumedAtUtc",
                table: "SyncDeviceApprovalGrants",
                columns: new[] { "ChallengeId", "ConsumedAtUtc" });

            migrationBuilder.CreateTable(
                name: "SyncAuthorKeyHandoffs",
                columns: table => new
                {
                    ChallengeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PendingDeviceRegistrationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EphemeralPublicKeySpki = table.Column<byte[]>(type: "bytea", nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    Tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliveredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncAuthorKeyHandoffs", x => x.ChallengeId);
                    table.CheckConstraint("CK_SyncAuthorKeyHandoffs_NonceLength", "octet_length(\"Nonce\") = 12");
                    table.CheckConstraint("CK_SyncAuthorKeyHandoffs_TagLength", "octet_length(\"Tag\") = 16");
                    table.CheckConstraint("CK_SyncAuthorKeyHandoffs_DeliveredNotBeforeCreated", "\"DeliveredAtUtc\" IS NULL OR \"DeliveredAtUtc\" >= \"CreatedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_SyncAuthorKeyHandoffs_AccountId_SyncAccounts_Id",
                        column: x => x.AccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncAuthorKeyHandoffs_ChallengeId_SyncDeviceApprovalGrants",
                        column: x => x.ChallengeId,
                        principalTable: "SyncDeviceApprovalGrants",
                        principalColumn: "ChallengeId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncAuthorKeyHandoffs_AccountId_Pending_SyncDeviceRegistrations",
                        columns: x => new { x.AccountId, x.PendingDeviceRegistrationId },
                        principalTable: "SyncDeviceRegistrations",
                        principalColumns: new[] { "AccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncAuthorKeyHandoffs_Account_Device_Delivered",
                table: "SyncAuthorKeyHandoffs",
                columns: new[] { "AccountId", "PendingDeviceRegistrationId", "DeliveredAtUtc" });

            migrationBuilder.CreateTable(
                name: "SyncAuthorKeyEscrows",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RecoveryVersion = table.Column<long>(type: "bigint", nullable: false),
                    AuthorKeyId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AuthorKeyVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FormatVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EphemeralSpki = table.Column<byte[]>(type: "bytea", nullable: false),
                    MasterKeyVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    ProtectedPayload = table.Column<byte[]>(type: "bytea", nullable: false),
                    Tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedByDeviceRegistrationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncAuthorKeyEscrows", x => new { x.AccountId, x.RecoveryVersion, x.AuthorKeyId });
                    table.CheckConstraint("CK_SyncAuthorKeyEscrows_NonceLength", "octet_length(\"Nonce\") = 12");
                    table.CheckConstraint("CK_SyncAuthorKeyEscrows_TagLength", "octet_length(\"Tag\") = 16");
                    table.CheckConstraint("CK_SyncAuthorKeyEscrows_RecoveryVersionPositive", "\"RecoveryVersion\" >= 1");
                    table.ForeignKey(
                        name: "FK_SyncAuthorKeyEscrows_AccountId_SyncAccounts_Id",
                        column: x => x.AccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncAuthorKeyEscrows_AccountId_RecoveryVersion_SyncRecoveryEnvelopes",
                        columns: x => new { x.AccountId, x.RecoveryVersion },
                        principalTable: "SyncRecoveryEnvelopes",
                        principalColumns: new[] { "AccountId", "RecoveryVersion" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncAuthorKeyEscrows_AccountId_CreatedBy_SyncDeviceRegistrations",
                        columns: x => new { x.AccountId, x.CreatedByDeviceRegistrationId },
                        principalTable: "SyncDeviceRegistrations",
                        principalColumns: new[] { "AccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncAuthorKeyEscrows_AccountId_CreatedBy",
                table: "SyncAuthorKeyEscrows",
                columns: new[] { "AccountId", "CreatedByDeviceRegistrationId" });

            migrationBuilder.CreateTable(
                name: "SyncLegacyContextExclusions",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LegacyChatId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncLegacyContextExclusions", x => new { x.AccountId, x.LegacyChatId });
                    table.ForeignKey(
                        name: "FK_SyncLegacyContextExclusions_AccountId_SyncAccounts_Id",
                        column: x => x.AccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncLegacyContextExclusions_LegacyChatId_Chats_Id",
                        column: x => x.LegacyChatId,
                        principalTable: "Chats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncLegacyContextExclusions_LegacyChatId",
                table: "SyncLegacyContextExclusions",
                column: "LegacyChatId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SyncLegacyContextExclusions");

            migrationBuilder.DropTable(name: "SyncAuthorKeyEscrows");

            migrationBuilder.DropTable(name: "SyncAuthorKeyHandoffs");

            migrationBuilder.DropTable(name: "SyncDeviceApprovalGrants");

            migrationBuilder.DropTable(name: "SyncRecoveryEnvelopes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SyncDeviceChallenges_LocalInstanceMatchesPurpose",
                table: "SyncDeviceChallenges");

            migrationBuilder.DropColumn(
                name: "LocalInstanceId",
                table: "SyncDeviceChallenges");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncAccounts_PreviousAccountId_SyncAccounts_Id",
                table: "SyncAccounts");

            migrationBuilder.DropIndex(
                name: "IX_SyncAccounts_LegacyUserId_FreshStartOperation",
                table: "SyncAccounts");

            migrationBuilder.DropColumn(
                name: "FreshStartOperationId",
                table: "SyncAccounts");

            migrationBuilder.DropIndex(
                name: "IX_SyncAccounts_PreviousAccountId",
                table: "SyncAccounts");

            migrationBuilder.DropColumn(
                name: "PreviousAccountId",
                table: "SyncAccounts");
        }
    }
}
