using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Database.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedHistoryAndArchives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Three additive tables; no existing column changes and no backfill. Each one is the
            // LEGACY-stream counterpart of a structure plan 02 already created in sequence space over
            // SyncMessages, which stays empty until stage 11 - every real message still lives in the
            // legacy Messages table, so an archive and a shared period can only be expressed over it.

            // Private per-device progress: this SQLite copy has stored that archived message. Nothing
            // public is derived from it - "Публичните отметки и unread в замразения чат остават
            // непроменени" - and it is keyed by LocalInstanceId, so clearing local data genuinely means
            // downloading the archive again.
            migrationBuilder.CreateTable(
                name: "SyncArchiveDeviceDeliveries",
                columns: table => new
                {
                    MembershipId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LocalInstanceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LegacyMessageId = table.Column<int>(type: "integer", nullable: false),
                    StoredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncArchiveDeviceDeliveries", x => new { x.MembershipId, x.LocalInstanceId, x.LegacyMessageId });
                    table.ForeignKey(
                        name: "FK_SyncArchiveDeviceDeliveries_LegacyMessageId_Messages_Id",
                        column: x => x.LegacyMessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncArchiveDeviceDeliveries_LocalInstanceId_SyncLocalInstances_Id",
                        column: x => x.LocalInstanceId,
                        principalTable: "SyncLocalInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncArchiveDeviceDeliveries_MembershipId_SyncMemberships_Id",
                        column: x => x.MembershipId,
                        principalTable: "SyncMemberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // The public statuses of a frozen stint's permitted messages, as they stood at its exit. The
            // live rows keep changing for the chat's still-active members, so without this copy the
            // archive would drift. Restrict on the message/user, because the snapshot IS the evidence of
            // what the removed member was allowed to see.
            migrationBuilder.CreateTable(
                name: "SyncMembershipLegacyStatusSnapshots",
                columns: table => new
                {
                    MembershipId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LegacyMessageId = table.Column<int>(type: "integer", nullable: false),
                    RecipientLegacyUserId = table.Column<int>(type: "integer", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CapturedAtChangeSequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMembershipLegacyStatusSnapshots", x => new { x.MembershipId, x.LegacyMessageId, x.RecipientLegacyUserId });
                    table.CheckConstraint("CK_SyncMembershipLegacyStatusSnapshots_CapturedAtChangeSequence_Positive", "\"CapturedAtChangeSequence\" > 0");
                    table.ForeignKey(
                        name: "FK_SyncMembershipLegacyStatusSnapshots_LegacyMessageId_Messages_Id",
                        column: x => x.LegacyMessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncMembershipLegacyStatusSnapshots_MembershipId_SyncMemberships_Id",
                        column: x => x.MembershipId,
                        principalTable: "SyncMemberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncMembershipLegacyStatusSnapshots_RecipientLegacyUserId_Users_Id",
                        column: x => x.RecipientLegacyUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // One disjoint window of OLDER history shared with a stint at the moment it was added. Several
            // rows exist exactly when the adder's own history had inaccessible gaps inside the chosen
            // period; an empty intersection is the ABSENCE of a row, never an inverted range - hence the
            // check constraint.
            migrationBuilder.CreateTable(
                name: "SyncMembershipSharedHistoryWindows",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    MembershipId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ThroughUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SelectedPeriod = table.Column<int>(type: "integer", nullable: false),
                    GrantedAtChangeSequence = table.Column<long>(type: "bigint", nullable: false),
                    GrantedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMembershipSharedHistoryWindows", x => x.Id);
                    table.CheckConstraint("CK_SyncMembershipSharedHistoryWindows_GrantedAtChangeSequence_Positive", "\"GrantedAtChangeSequence\" > 0");
                    table.CheckConstraint("CK_SyncMembershipSharedHistoryWindows_Range_Valid", "\"ThroughUtc\" >= \"FromUtc\"");
                    table.ForeignKey(
                        name: "FK_SyncMembershipSharedHistoryWindows_MembershipId_SyncMemberships_Id",
                        column: x => x.MembershipId,
                        principalTable: "SyncMemberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncArchiveDeviceDeliveries_LegacyMessageId",
                table: "SyncArchiveDeviceDeliveries",
                column: "LegacyMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncArchiveDeviceDeliveries_LocalInstanceId",
                table: "SyncArchiveDeviceDeliveries",
                column: "LocalInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncMembershipLegacyStatusSnapshots_LegacyMessageId",
                table: "SyncMembershipLegacyStatusSnapshots",
                column: "LegacyMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncMembershipLegacyStatusSnapshots_RecipientLegacyUserId",
                table: "SyncMembershipLegacyStatusSnapshots",
                column: "RecipientLegacyUserId");

            // Disjoint by construction, so distinct lower bounds; unique so a retried add that somehow
            // reached the insert twice cannot silently double a window.
            migrationBuilder.CreateIndex(
                name: "IX_SyncMembershipSharedHistoryWindows_MembershipId_FromUtc",
                table: "SyncMembershipSharedHistoryWindows",
                columns: new[] { "MembershipId", "FromUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncArchiveDeviceDeliveries");

            migrationBuilder.DropTable(
                name: "SyncMembershipLegacyStatusSnapshots");

            migrationBuilder.DropTable(
                name: "SyncMembershipSharedHistoryWindows");
        }
    }
}
