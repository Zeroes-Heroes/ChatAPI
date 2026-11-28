using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Database.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipientStatusAndUnread : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Two additive tables plus two nullable columns; no existing column changes and no backfill.
            // The legacy MessagesStatus log keeps its meaning and keeps being written (as the projection
            // the current UI reads), so every status that existed before this migration still reads
            // exactly as it did - these rows are the LOGICAL model those marks are derived from from now
            // on, and they start empty.
            migrationBuilder.CreateTable(
                name: "SyncLegacyMessageRecipientStates",
                columns: table => new
                {
                    MembershipId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LegacyMessageId = table.Column<int>(type: "integer", nullable: false),
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SeenReason = table.Column<int>(type: "integer", nullable: true),
                    SeenClientReportedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncLegacyMessageRecipientStates", x => new { x.MembershipId, x.LegacyMessageId });
                    table.CheckConstraint("CK_SyncLegacyMessageRecipientStates_SeenPair", "(\"SeenAtUtc\" IS NULL) = (\"SeenReason\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_SyncLegacyMsgRecipientStates_ChatId_AccountId_MembershipId",
                        columns: x => new { x.ChatId, x.AccountId, x.MembershipId },
                        principalTable: "SyncMemberships",
                        principalColumns: new[] { "ChatId", "AccountId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncLegacyMsgRecipientStates_LegacyMessageId_Messages_Id",
                        column: x => x.LegacyMessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncLegacyMessageDeviceStates",
                columns: table => new
                {
                    LocalInstanceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LegacyMessageId = table.Column<int>(type: "integer", nullable: false),
                    DeviceRegistrationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MembershipId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CiphertextStoredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ObservedSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncLegacyMessageDeviceStates", x => new { x.LocalInstanceId, x.LegacyMessageId });
                    table.ForeignKey(
                        name: "FK_SyncLegacyMsgDeviceStates_ChatId_AccountId_MembershipId",
                        columns: x => new { x.ChatId, x.AccountId, x.MembershipId },
                        principalTable: "SyncMemberships",
                        principalColumns: new[] { "ChatId", "AccountId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncLegacyMsgDeviceStates_AccountId_DeviceRegistrationId",
                        columns: x => new { x.AccountId, x.DeviceRegistrationId },
                        principalTable: "SyncDeviceRegistrations",
                        principalColumns: new[] { "AccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncLegacyMsgDeviceStates_DeviceRegistrationId_InstanceId",
                        columns: x => new { x.DeviceRegistrationId, x.LocalInstanceId },
                        principalTable: "SyncLocalInstances",
                        principalColumns: new[] { "DeviceRegistrationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncLegacyMsgDeviceStates_LegacyMessageId_Messages_Id",
                        column: x => x.LegacyMessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncLegacyMessageRecipientStates_ChatId_LegacyMessageId",
                table: "SyncLegacyMessageRecipientStates",
                columns: new[] { "ChatId", "LegacyMessageId" });

            // Needed by the composite FKs above as well as by the per-account reads ("what does this
            // person hold in this chat"), which the leading columns of these indexes also answer. Every
            // name here is explicit and within PostgreSQL's 63-character identifier limit, so nothing is
            // silently truncated at creation.
            migrationBuilder.CreateIndex(
                name: "IX_SyncLegacyMsgRecipientStates_ChatId_AccountId_MembershipId",
                table: "SyncLegacyMessageRecipientStates",
                columns: new[] { "ChatId", "AccountId", "MembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncLegacyMsgDeviceStates_ChatId_AccountId_MembershipId",
                table: "SyncLegacyMessageDeviceStates",
                columns: new[] { "ChatId", "AccountId", "MembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncLegacyMsgDeviceStates_AccountId_DeviceRegistrationId",
                table: "SyncLegacyMessageDeviceStates",
                columns: new[] { "AccountId", "DeviceRegistrationId" });

            // The instance FK's own index: the primary key leads with LocalInstanceId, so nothing else
            // covers (DeviceRegistrationId, LocalInstanceId).
            migrationBuilder.CreateIndex(
                name: "IX_SyncLegacyMsgDeviceStates_DeviceRegistrationId_InstanceId",
                table: "SyncLegacyMessageDeviceStates",
                columns: new[] { "DeviceRegistrationId", "LocalInstanceId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncLegacyMessageRecipientStates_LegacyMessageId",
                table: "SyncLegacyMessageRecipientStates",
                column: "LegacyMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncLegacyMessageDeviceStates_LegacyMessageId",
                table: "SyncLegacyMessageDeviceStates",
                column: "LegacyMessageId");

            // Unread is NOT given a legacy twin table: a membership stint has exactly one counter, and a
            // second table would mean two. The two pointers are what cannot be expressed in the
            // sequence-space columns while real messages are legacy rows.
            migrationBuilder.AddColumn<int>(
                name: "OldestUnreadLegacyMessageId",
                table: "SyncUnreadStates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LatestUnreadLegacyMessageId",
                table: "SyncUnreadStates",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncUnreadStates_OldestUnreadLegacyMessageId",
                table: "SyncUnreadStates",
                column: "OldestUnreadLegacyMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncUnreadStates_LatestUnreadLegacyMessageId",
                table: "SyncUnreadStates",
                column: "LatestUnreadLegacyMessageId");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncUnreadStates_OldestUnreadLegacyMessageId_Messages_Id",
                table: "SyncUnreadStates",
                column: "OldestUnreadLegacyMessageId",
                principalTable: "Messages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncUnreadStates_LatestUnreadLegacyMessageId_Messages_Id",
                table: "SyncUnreadStates",
                column: "LatestUnreadLegacyMessageId",
                principalTable: "Messages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncUnreadStates_OldestUnreadLegacyMessageId_Messages_Id",
                table: "SyncUnreadStates");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncUnreadStates_LatestUnreadLegacyMessageId_Messages_Id",
                table: "SyncUnreadStates");

            migrationBuilder.DropIndex(
                name: "IX_SyncUnreadStates_OldestUnreadLegacyMessageId",
                table: "SyncUnreadStates");

            migrationBuilder.DropIndex(
                name: "IX_SyncUnreadStates_LatestUnreadLegacyMessageId",
                table: "SyncUnreadStates");

            migrationBuilder.DropColumn(
                name: "OldestUnreadLegacyMessageId",
                table: "SyncUnreadStates");

            migrationBuilder.DropColumn(
                name: "LatestUnreadLegacyMessageId",
                table: "SyncUnreadStates");

            migrationBuilder.DropTable(
                name: "SyncLegacyMessageDeviceStates");

            migrationBuilder.DropTable(
                name: "SyncLegacyMessageRecipientStates");
        }
    }
}
