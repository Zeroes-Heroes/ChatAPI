using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatAPI.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MapUserLoginCodeToUserDevice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserLoginCodes_UserId_Users_Id",
                table: "UserLoginCodes");

            migrationBuilder.RenameColumn(
                name: "UserId",
                table: "UserLoginCodes",
                newName: "UserDeviceId");

            migrationBuilder.AddForeignKey(
                name: "FK_UserLoginCodes_UserDeviceId_UserDevices_Id",
                table: "UserLoginCodes",
                column: "UserDeviceId",
                principalTable: "UserDevices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserLoginCodes_UserDeviceId_UserDevices_Id",
                table: "UserLoginCodes");

            migrationBuilder.RenameColumn(
                name: "UserDeviceId",
                table: "UserLoginCodes",
                newName: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_UserLoginCodes_UserId_Users_Id",
                table: "UserLoginCodes",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
