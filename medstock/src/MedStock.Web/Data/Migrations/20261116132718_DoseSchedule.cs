using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedStock.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DoseSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Every",
                table: "Medications",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EveryFrom",
                table: "Medications",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Moments",
                table: "Medications",
                type: "TEXT",
                nullable: false,
                defaultValueSql: "'[]'");

            migrationBuilder.AddColumn<TimeOnly>(
                name: "WeeklyTime",
                table: "Medications",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "UntimedTakenFrom",
                table: "GlobalSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: new TimeOnly(20, 0, 0));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Every",
                table: "Medications");

            migrationBuilder.DropColumn(
                name: "EveryFrom",
                table: "Medications");

            migrationBuilder.DropColumn(
                name: "Moments",
                table: "Medications");

            migrationBuilder.DropColumn(
                name: "WeeklyTime",
                table: "Medications");

            migrationBuilder.DropColumn(
                name: "UntimedTakenFrom",
                table: "GlobalSettings");
        }
    }
}
