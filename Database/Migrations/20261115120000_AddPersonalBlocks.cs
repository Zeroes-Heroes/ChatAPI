using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Database.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalBlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One additive table; no existing column changes and no backfill. Rows that already reached
            // FriendshipStatus.Blocked on the legacy Friendships table keep working exactly as before -
            // Services/Contacts/PersonalRelationDirectory unions them in, deriving the blocker the way
            // stage 07 did (the status can only be set by the row's TargetUserId). This table is what
            // lets the request's SENDER block too, and what lets a block be lifted without guessing the
            // status it replaced.
            migrationBuilder.CreateTable(
                name: "SyncPersonalBlocks",
                columns: table => new
                {
                    BlockerUserId = table.Column<int>(type: "integer", nullable: false),
                    SubjectUserId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LiftedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    // The ordered pair is the identity of the relation: a re-block reuses the row, which is
                    // what makes block/unblock idempotent under a lost response and a retry.
                    table.PrimaryKey("PK_SyncPersonalBlocks", x => new { x.BlockerUserId, x.SubjectUserId });
                    table.CheckConstraint("CK_SyncPersonalBlocks_NotSelf", "\"BlockerUserId\" <> \"SubjectUserId\"");
                    table.ForeignKey(
                        name: "FK_SyncPersonalBlocks_BlockerUserId_Users_Id",
                        column: x => x.BlockerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncPersonalBlocks_SubjectUserId_Users_Id",
                        column: x => x.SubjectUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // "Who has blocked me", the direction the correspondence gate and the profile resolver ask for
            // a whole candidate set; the primary key only serves "who have I blocked".
            migrationBuilder.CreateIndex(
                name: "IX_SyncPersonalBlocks_SubjectUserId_BlockerUserId",
                table: "SyncPersonalBlocks",
                columns: new[] { "SubjectUserId", "BlockerUserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncPersonalBlocks");
        }
    }
}
