using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Database.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthorKeyRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One additive table; no existing column changes and no backfill. Stage 11's acceptance path
            // verifies a signed package against THIS registry and nothing else, so an empty table simply
            // means no author context has been published yet - never a package accepted unverified.
            migrationBuilder.CreateTable(
                name: "SyncAuthorKeys",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AuthorKeyId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AuthorKeyVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Spki = table.Column<byte[]>(type: "bytea", nullable: false),
                    RegisteredByDeviceRegistrationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    // The complete wire AuthorKeyReference is the key, so one reference can never hold two
                    // different public keys and a rotation must introduce a new version.
                    table.PrimaryKey("PK_SyncAuthorKeys", x => new { x.AccountId, x.ChatId, x.AuthorKeyId, x.AuthorKeyVersion });
                    table.CheckConstraint("CK_SyncAuthorKeys_SpkiLength", "octet_length(\"Spki\") = 91");
                    table.ForeignKey(
                        name: "FK_SyncAuthorKeys_AccountId_SyncAccounts_Id",
                        column: x => x.AccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncAuthorKeys_ChatId_SyncChats_Id",
                        column: x => x.ChatId,
                        principalTable: "SyncChats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncAuthorKeys_AccountId_RegisteredBy_SyncDeviceRegs",
                        columns: x => new { x.AccountId, x.RegisteredByDeviceRegistrationId },
                        principalTable: "SyncDeviceRegistrations",
                        principalColumns: new[] { "AccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            // "Which author contexts does this account hold in this chat" - the enumeration a delivery or
            // escrow path needs; the per-reference lookup acceptance performs is the primary key itself.
            migrationBuilder.CreateIndex(
                name: "IX_SyncAuthorKeys_ChatId_AccountId",
                table: "SyncAuthorKeys",
                columns: new[] { "ChatId", "AccountId" });

            // The registering-device FK's own index: the primary key leads with AccountId but never
            // mentions RegisteredByDeviceRegistrationId.
            migrationBuilder.CreateIndex(
                name: "IX_SyncAuthorKeys_AccountId_RegisteredByDeviceRegistrationId",
                table: "SyncAuthorKeys",
                columns: new[] { "AccountId", "RegisteredByDeviceRegistrationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncAuthorKeys");
        }
    }
}
