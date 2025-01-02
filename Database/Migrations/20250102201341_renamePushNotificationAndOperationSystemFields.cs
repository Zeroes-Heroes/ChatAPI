using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Database.Migrations
{
    /// <inheritdoc />
    public partial class renamePushNotificationAndOperationSystemFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PushNotification_OS_OperationSystem_Id",
                table: "PushNotification");

            migrationBuilder.DropForeignKey(
                name: "FK_PushNotification_UserId_Users_Id",
                table: "PushNotification");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PushNotification",
                table: "PushNotification");

            migrationBuilder.DropPrimaryKey(
                name: "PK_OperationSystem",
                table: "OperationSystem");

            migrationBuilder.RenameTable(
                name: "PushNotification",
                newName: "PushNotifications");

            migrationBuilder.RenameTable(
                name: "OperationSystem",
                newName: "OperationSystems");

            migrationBuilder.RenameColumn(
                name: "IsTurnOnNotification",
                table: "PushNotifications",
                newName: "IsNotificationEnabled");

            migrationBuilder.RenameIndex(
                name: "IX_PushNotification_UserId",
                table: "PushNotifications",
                newName: "IX_PushNotifications_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_PushNotification_OS",
                table: "PushNotifications",
                newName: "IX_PushNotifications_OS");

            migrationBuilder.RenameIndex(
                name: "IX_PushNotification_DeviceId",
                table: "PushNotifications",
                newName: "IX_PushNotifications_DeviceId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PushNotifications",
                table: "PushNotifications",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_OperationSystems",
                table: "OperationSystems",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PushNotifications_OS_OperationSystem_Id",
                table: "PushNotifications",
                column: "OS",
                principalTable: "OperationSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PushNotifications_UserId_Users_Id",
                table: "PushNotifications",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PushNotifications_OS_OperationSystem_Id",
                table: "PushNotifications");

            migrationBuilder.DropForeignKey(
                name: "FK_PushNotifications_UserId_Users_Id",
                table: "PushNotifications");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PushNotifications",
                table: "PushNotifications");

            migrationBuilder.DropPrimaryKey(
                name: "PK_OperationSystems",
                table: "OperationSystems");

            migrationBuilder.RenameTable(
                name: "PushNotifications",
                newName: "PushNotification");

            migrationBuilder.RenameTable(
                name: "OperationSystems",
                newName: "OperationSystem");

            migrationBuilder.RenameColumn(
                name: "IsNotificationEnabled",
                table: "PushNotification",
                newName: "IsTurnOnNotification");

            migrationBuilder.RenameIndex(
                name: "IX_PushNotifications_UserId",
                table: "PushNotification",
                newName: "IX_PushNotification_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_PushNotifications_OS",
                table: "PushNotification",
                newName: "IX_PushNotification_OS");

            migrationBuilder.RenameIndex(
                name: "IX_PushNotifications_DeviceId",
                table: "PushNotification",
                newName: "IX_PushNotification_DeviceId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PushNotification",
                table: "PushNotification",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_OperationSystem",
                table: "OperationSystem",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PushNotification_OS_OperationSystem_Id",
                table: "PushNotification",
                column: "OS",
                principalTable: "OperationSystem",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PushNotification_UserId_Users_Id",
                table: "PushNotification",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
