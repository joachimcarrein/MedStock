using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedStock.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class UserLastCulture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastCulture",
                table: "UserSettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastCulture",
                table: "UserSettings");
        }
    }
}
