using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedStock.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Reminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReminderProfileIds",
                table: "UserSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "MedicationReminders",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    MedicationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MedicationReminders", x => new { x.UserId, x.MedicationId });
                    table.ForeignKey(
                        name: "FK_MedicationReminders_Medications_MedicationId",
                        column: x => x.MedicationId,
                        principalTable: "Medications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReminderLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Time = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnoozedReminders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Time = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    DueAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnoozedReminders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MedicationReminders_MedicationId",
                table: "MedicationReminders",
                column: "MedicationId");

            migrationBuilder.CreateIndex(
                name: "IX_ReminderLogs_UserId_ProfileId_Date_Time",
                table: "ReminderLogs",
                columns: new[] { "UserId", "ProfileId", "Date", "Time" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SnoozedReminders_DueAt",
                table: "SnoozedReminders",
                column: "DueAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MedicationReminders");

            migrationBuilder.DropTable(
                name: "ReminderLogs");

            migrationBuilder.DropTable(
                name: "SnoozedReminders");

            migrationBuilder.DropColumn(
                name: "ReminderProfileIds",
                table: "UserSettings");
        }
    }
}
