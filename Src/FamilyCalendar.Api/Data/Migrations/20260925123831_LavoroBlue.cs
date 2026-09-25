using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyCalendar.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LavoroBlue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "Labels",
                keyColumn: "Id",
                keyValue: 2,
                column: "Color",
                value: "#3b82f6");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "Labels",
                keyColumn: "Id",
                keyValue: 2,
                column: "Color",
                value: "#ff9a1f");
        }
    }
}
