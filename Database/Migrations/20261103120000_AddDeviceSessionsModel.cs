using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Database.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceSessionsModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SyncDeviceRegistrations has never been written to by any shipped code (stage 02 created
            // it as an identity anchor only, explicitly deferring trust - see its own doc comment).
            // Fail loudly here, the same way AddMessageKeyAadFields does for SyncMessageKeys, instead
            // of letting a pre-existing row silently collide with the NOT NULL columns below.
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM ""SyncDeviceRegistrations"") THEN
                        RAISE EXCEPTION 'AddDeviceSessionsModel requires an empty SyncDeviceRegistrations table; no pre-plan05 trust/key format exists to backfill.';
                    END IF;
                END $$;
            ");

            migrationBuilder.AddColumn<string>(
                name: "KeyId",
                table: "SyncDeviceRegistrations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "PublicKeySpki",
                table: "SyncDeviceRegistrations",
                type: "bytea",
                nullable: false,
                defaultValueSql: "'\\x'::bytea");

            migrationBuilder.AddColumn<int>(
                name: "TrustState",
                table: "SyncDeviceRegistrations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "TrustVersion",
                table: "SyncDeviceRegistrations",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<DateTime>(
                name: "RevokedAtUtc",
                table: "SyncDeviceRegistrations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFirstDevice",
                table: "SyncDeviceRegistrations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedByDeviceRegistrationId",
                table: "SyncDeviceRegistrations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SyncDeviceRegistrations_RevokedAtUtc_RequiresRevokedState",
                table: "SyncDeviceRegistrations",
                sql: "(\"TrustState\" = 2 AND \"RevokedAtUtc\" IS NOT NULL) OR (\"TrustState\" != 2 AND \"RevokedAtUtc\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_SyncDeviceRegistrations_AccountId_FirstDevice",
                table: "SyncDeviceRegistrations",
                column: "AccountId",
                unique: true,
                filter: "\"IsFirstDevice\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_SyncDeviceRegistrations_AccountId_ApprovedByDeviceRegistrationId",
                table: "SyncDeviceRegistrations",
                columns: new[] { "AccountId", "ApprovedByDeviceRegistrationId" });

            migrationBuilder.AddForeignKey(
                name: "FK_SyncDeviceRegistrations_AccountId_ApprovedBy_SyncDeviceRegistrations",
                table: "SyncDeviceRegistrations",
                columns: new[] { "AccountId", "ApprovedByDeviceRegistrationId" },
                principalTable: "SyncDeviceRegistrations",
                principalColumns: new[] { "AccountId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddColumn<bool>(
                name: "RevocationBarrierActive",
                table: "SyncAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "SyncDeviceChallenges",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Purpose = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeviceRegistrationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    KeyId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PublicKeySpki = table.Column<byte[]>(type: "bytea", nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SessionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RefreshOperationId = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncDeviceChallenges", x => x.Id);
                    table.CheckConstraint("CK_SyncDeviceChallenges_ExpiresAfterCreated", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_SyncDeviceChallenges_ConsumedNotBeforeCreated", "\"ConsumedAtUtc\" IS NULL OR \"ConsumedAtUtc\" >= \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_SyncDeviceChallenges_SessionBindingFieldsMatchPurpose",
                        "(\"Purpose\" = 2 AND \"SessionId\" IS NOT NULL AND \"RefreshOperationId\" IS NOT NULL) OR (\"Purpose\" != 2 AND \"SessionId\" IS NULL AND \"RefreshOperationId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_SyncDeviceChallenges_AccountId_SyncAccounts_Id",
                        column: x => x.AccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncDeviceChallenges_AccountId_DeviceRegistrationId_SyncDeviceRegistrations",
                        columns: x => new { x.AccountId, x.DeviceRegistrationId },
                        principalTable: "SyncDeviceRegistrations",
                        principalColumns: new[] { "AccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncDeviceChallenges_Nonce",
                table: "SyncDeviceChallenges",
                column: "Nonce",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncDeviceChallenges_Id_ConsumedAtUtc",
                table: "SyncDeviceChallenges",
                columns: new[] { "Id", "ConsumedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncDeviceChallenges_AccountId_DeviceRegistrationId",
                table: "SyncDeviceChallenges",
                columns: new[] { "AccountId", "DeviceRegistrationId" });

            migrationBuilder.CreateTable(
                name: "SyncDeviceSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeviceRegistrationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LocalInstanceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TrustVersionAtIssuance = table.Column<long>(type: "bigint", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CurrentRefreshTokenHash = table.Column<byte[]>(type: "bytea", nullable: true),
                    RefreshTokenExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRotationOperationId = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    LastRotationAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncDeviceSessions", x => x.Id);
                    table.UniqueConstraint("AK_SyncDeviceSessions_AccountId_Id", x => new { x.AccountId, x.Id });
                    table.CheckConstraint("CK_SyncDeviceSessions_RefreshHashRequiresExpiry",
                        "(\"CurrentRefreshTokenHash\" IS NULL) = (\"RefreshTokenExpiresAtUtc\" IS NULL)");
                    table.CheckConstraint("CK_SyncDeviceSessions_EndedNotBeforeCreated", "\"EndedAtUtc\" IS NULL OR \"EndedAtUtc\" >= \"CreatedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_SyncDeviceSessions_AccountId_SyncAccounts_Id",
                        column: x => x.AccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncDeviceSessions_AccountId_DeviceRegistrationId_SyncDeviceRegistrations",
                        columns: x => new { x.AccountId, x.DeviceRegistrationId },
                        principalTable: "SyncDeviceRegistrations",
                        principalColumns: new[] { "AccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncDeviceSessions_DeviceRegistrationId_LocalInstanceId_SyncLocalInstances",
                        columns: x => new { x.DeviceRegistrationId, x.LocalInstanceId },
                        principalTable: "SyncLocalInstances",
                        principalColumns: new[] { "DeviceRegistrationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncDeviceSessions_AccountId_DeviceRegistrationId",
                table: "SyncDeviceSessions",
                columns: new[] { "AccountId", "DeviceRegistrationId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncDeviceSessions_DeviceRegistrationId_LocalInstanceId",
                table: "SyncDeviceSessions",
                columns: new[] { "DeviceRegistrationId", "LocalInstanceId" });

            migrationBuilder.CreateTable(
                name: "SyncRefreshTokenRotations",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RefreshOperationId = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    OldRefreshTokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    NewRefreshTokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReplayExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProtectedReplayPayload = table.Column<byte[]>(type: "bytea", nullable: true),
                    ProtectedReplayNonce = table.Column<byte[]>(type: "bytea", nullable: true),
                    ProtectedReplayTag = table.Column<byte[]>(type: "bytea", nullable: true),
                    ProtectedReplayMasterKeyVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRefreshTokenRotations", x => new { x.SessionId, x.RefreshOperationId });
                    table.CheckConstraint("CK_SyncRefreshTokenRotations_ReplayExpiresNotBeforeCreated", "\"ReplayExpiresAtUtc\" >= \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_SyncRefreshTokenRotations_ReplayPayloadFieldsTogether",
                        "(\"ProtectedReplayPayload\" IS NULL AND \"ProtectedReplayNonce\" IS NULL AND \"ProtectedReplayTag\" IS NULL AND \"ProtectedReplayMasterKeyVersion\" IS NULL)" +
                        " OR (\"ProtectedReplayPayload\" IS NOT NULL AND \"ProtectedReplayNonce\" IS NOT NULL AND \"ProtectedReplayTag\" IS NOT NULL AND \"ProtectedReplayMasterKeyVersion\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_SyncRefreshTokenRotations_SessionId_SyncDeviceSessions_Id",
                        column: x => x.SessionId,
                        principalTable: "SyncDeviceSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncRefreshTokenRotations_SessionId_OldHash",
                table: "SyncRefreshTokenRotations",
                columns: new[] { "SessionId", "OldRefreshTokenHash" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncRefreshTokenRotations_SessionId_NewHash",
                table: "SyncRefreshTokenRotations",
                columns: new[] { "SessionId", "NewRefreshTokenHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SyncRefreshTokenRotations");
            migrationBuilder.DropTable(name: "SyncDeviceSessions");
            migrationBuilder.DropTable(name: "SyncDeviceChallenges");

            migrationBuilder.DropColumn(name: "RevocationBarrierActive", table: "SyncAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncDeviceRegistrations_AccountId_ApprovedBy_SyncDeviceRegistrations",
                table: "SyncDeviceRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_SyncDeviceRegistrations_AccountId_ApprovedByDeviceRegistrationId",
                table: "SyncDeviceRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_SyncDeviceRegistrations_AccountId_FirstDevice",
                table: "SyncDeviceRegistrations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SyncDeviceRegistrations_RevokedAtUtc_RequiresRevokedState",
                table: "SyncDeviceRegistrations");

            migrationBuilder.DropColumn(name: "ApprovedByDeviceRegistrationId", table: "SyncDeviceRegistrations");
            migrationBuilder.DropColumn(name: "IsFirstDevice", table: "SyncDeviceRegistrations");
            migrationBuilder.DropColumn(name: "RevokedAtUtc", table: "SyncDeviceRegistrations");
            migrationBuilder.DropColumn(name: "TrustVersion", table: "SyncDeviceRegistrations");
            migrationBuilder.DropColumn(name: "TrustState", table: "SyncDeviceRegistrations");
            migrationBuilder.DropColumn(name: "PublicKeySpki", table: "SyncDeviceRegistrations");
            migrationBuilder.DropColumn(name: "KeyId", table: "SyncDeviceRegistrations");
        }
    }
}
