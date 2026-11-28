using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Database.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptOutboxDiscriminator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows and older message producers remain Message (SyncChangeKind value 1).
            // Status delivery must retain its kind/reference even after its journal entry is pruned.
            migrationBuilder.AddColumn<int>(
                name: "ChangeKind",
                table: "SyncOutbox",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "SecondaryId",
                table: "SyncOutbox",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "SecondaryId", table: "SyncOutbox");
            migrationBuilder.DropColumn(name: "ChangeKind", table: "SyncOutbox");
        }
    }
}
