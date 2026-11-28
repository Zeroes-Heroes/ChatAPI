using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Database.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageKeyAadFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SyncMessageKeys has never been written to by any shipped code in any known environment
            // (this migration/stage is the first to create rows here) - the "" defaults just below
            // rely on that. Fail loudly and explicitly here if that assumption is ever wrong, instead
            // of letting a pre-existing row silently collide with the composite FK added below as an
            // incidental, cryptic failure. There is no backfill/rewrap path for a pre-plan04 row
            // format because no such format has ever existed.
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM ""SyncMessageKeys"") THEN
                        RAISE EXCEPTION 'AddMessageKeyAadFields requires an empty SyncMessageKeys table; no pre-plan04 row format exists to backfill/rewrap.';
                    END IF;
                END $$;
            ");

            migrationBuilder.AddColumn<string>(
                name: "ChatId",
                table: "SyncMessageKeys",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FormatVersion",
                table: "SyncMessageKeys",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "SyncMessageKeys",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            // The independent single-column MessageId FK was created by the prior
            // AddSyncServerDataModel migration (before ChatId existed on this table). Superseded
            // below by one composite FK (ChatId, MessageId) -> SyncMessages(ChatId, Id), anchored on
            // the existing AK_SyncMessages_ChatId_Id alternate key - mirrors SenderMembership's own
            // composite-FK pattern in this same codebase, so ChatId can never disagree with the real
            // chat of the message this protected key belongs to (review-report.md HIGH #5). The
            // unique index is required for this 1:1 relationship's FK (see
            // SyncMessageKeyConfiguration.cs).
            migrationBuilder.DropForeignKey(
                name: "FK_SyncMessageKeys_MessageId_SyncMessages_Id",
                table: "SyncMessageKeys");

            migrationBuilder.CreateIndex(
                name: "IX_SyncMessageKeys_ChatId_MessageId",
                table: "SyncMessageKeys",
                columns: new[] { "ChatId", "MessageId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncMessageKeys_ChatId_MessageId_SyncMessages_ChatId_Id",
                table: "SyncMessageKeys",
                columns: new[] { "ChatId", "MessageId" },
                principalTable: "SyncMessages",
                principalColumns: new[] { "ChatId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncMessageKeys_ChatId_MessageId_SyncMessages_ChatId_Id",
                table: "SyncMessageKeys");

            migrationBuilder.DropIndex(
                name: "IX_SyncMessageKeys_ChatId_MessageId",
                table: "SyncMessageKeys");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncMessageKeys_MessageId_SyncMessages_Id",
                table: "SyncMessageKeys",
                column: "MessageId",
                principalTable: "SyncMessages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.DropColumn(
                name: "ChatId",
                table: "SyncMessageKeys");

            migrationBuilder.DropColumn(
                name: "FormatVersion",
                table: "SyncMessageKeys");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "SyncMessageKeys");
        }
    }
}
