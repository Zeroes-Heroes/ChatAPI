using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Database.Migrations
{
    /// <inheritdoc />
    public partial class AddChatMembershipModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Both columns are nullable and carry no backfill requirement: null means exactly what a
            // pre-stage-07 row means (no recorded adder; history from the beginning of the chat), so no
            // precondition check is needed here - unlike AddRecoveryAndApprovalModel's NOT NULL column.

            // The actor of this stint's MembershipEvent.Added - the counterpart of the existing
            // EndedByAccountId, so the wire event's ActorAccountContextId can be derived per recipient
            // without a second copy on SyncChatChanges.
            migrationBuilder.AddColumn<string>(
                name: "AddedByAccountId",
                table: "SyncMemberships",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncMemberships_AddedByAccountId",
                table: "SyncMemberships",
                column: "AddedByAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncMemberships_AddedByAccountId_SyncAccounts_Id",
                table: "SyncMemberships",
                column: "AddedByAccountId",
                principalTable: "SyncAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Lower bound of the legacy message history this stint may read while real messages still
            // live in the legacy Messages table (sequence-numbered SyncMessages arrive in stage 11).
            // Null = from the beginning of the chat (founder / stint projected from a legacy chat).
            migrationBuilder.AddColumn<DateTime>(
                name: "LegacyHistoryFromUtc",
                table: "SyncMemberships",
                type: "timestamp with time zone",
                nullable: true);

            // plan/07-membership.md: "Активен участник не се добавя повторно за разширяване на историята".
            // Enforced in the database so a concurrent double add can never produce two active stints
            // (whose history ranges would silently widen that member's access); frozen stints are
            // deliberately outside the filter, since leave/rejoin accumulates arbitrarily many.
            migrationBuilder.CreateIndex(
                name: "IX_SyncMemberships_ChatId_AccountId_Active",
                table: "SyncMemberships",
                columns: new[] { "ChatId", "AccountId" },
                unique: true,
                filter: "\"State\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SyncMemberships_ChatId_AccountId_Active",
                table: "SyncMemberships");

            migrationBuilder.DropColumn(
                name: "LegacyHistoryFromUtc",
                table: "SyncMemberships");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncMemberships_AddedByAccountId_SyncAccounts_Id",
                table: "SyncMemberships");

            migrationBuilder.DropIndex(
                name: "IX_SyncMemberships_AddedByAccountId",
                table: "SyncMemberships");

            migrationBuilder.DropColumn(
                name: "AddedByAccountId",
                table: "SyncMemberships");
        }
    }
}
