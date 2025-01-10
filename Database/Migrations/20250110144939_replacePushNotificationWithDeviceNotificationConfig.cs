using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Database.Migrations
{
    /// <inheritdoc />
    public partial class replacePushNotificationWithDeviceNotificationConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushNotifications");

            migrationBuilder.AddColumn<int>(
                name: "DeviceNotificationsConfigId",
                table: "UserDevices",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeviceNotifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    OperatingSystem = table.Column<int>(type: "integer", nullable: false),
                    Token = table.Column<string>(type: "text", nullable: false),
                    IsNotificationEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UserEntityId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceNotificationConfig_UserDevices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "UserDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeviceNotificationConfig_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeviceNotifications_Users_UserEntityId",
                        column: x => x.UserEntityId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserDevices_DeviceNotificationsConfigId",
                table: "UserDevices",
                column: "DeviceNotificationsConfigId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceNotifications_DeviceId",
                table: "DeviceNotifications",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceNotifications_UserEntityId",
                table: "DeviceNotifications",
                column: "UserEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceNotifications_UserId",
                table: "DeviceNotifications",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_UserDevices_DeviceNotifications_DeviceNotificationsConfigId",
                table: "UserDevices",
                column: "DeviceNotificationsConfigId",
                principalTable: "DeviceNotifications",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserDevices_DeviceNotifications_DeviceNotificationsConfigId",
                table: "UserDevices");

            migrationBuilder.DropTable(
                name: "DeviceNotifications");

            migrationBuilder.DropIndex(
                name: "IX_UserDevices_DeviceNotificationsConfigId",
                table: "UserDevices");

            migrationBuilder.DropColumn(
                name: "DeviceNotificationsConfigId",
                table: "UserDevices");

            migrationBuilder.CreateTable(
                name: "PushNotifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    IsNotificationEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    OperatingSystem = table.Column<int>(type: "integer", nullable: false),
                    Token = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushNotification_DeviceId_UserDevice_Id",
                        column: x => x.DeviceId,
                        principalTable: "UserDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PushNotifications_UserId_Users_Id",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PushNotifications_DeviceId",
                table: "PushNotifications",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushNotifications_UserId",
                table: "PushNotifications",
                column: "UserId");
        }
    }
}
