using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatAPI.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserLoginCodesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserLoginCodes",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    SecretLoginCode = table.Column<Guid>(type: "uuid", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserLoginCodes", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserLoginCodes_UserId_Users_Id",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserLoginCodes");
        }
    }
}
