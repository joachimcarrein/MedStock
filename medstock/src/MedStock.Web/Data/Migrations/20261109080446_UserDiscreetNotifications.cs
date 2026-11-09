using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedStock.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class UserDiscreetNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DiscreetNotifications",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscreetNotifications",
                table: "UserSettings");
        }
    }
}
