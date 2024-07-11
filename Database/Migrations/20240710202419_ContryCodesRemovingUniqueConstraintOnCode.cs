using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Database.Migrations
{
    /// <inheritdoc />
    public partial class ContryCodesRemovingUniqueConstraintOnCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CountryCodes_Code",
                table: "CountryCodes");

            migrationBuilder.CreateIndex(
                name: "IX_CountryCodes_Code",
                table: "CountryCodes",
                column: "Code");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CountryCodes_Code",
                table: "CountryCodes");

            migrationBuilder.CreateIndex(
                name: "IX_CountryCodes_Code",
                table: "CountryCodes",
                column: "Code",
                unique: true);
        }
    }
}
