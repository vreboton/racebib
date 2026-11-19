using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RaceTiming.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRunnerRegistrationManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "Birthdate",
                table: "Runners",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmergencyContact",
                table: "Runners",
                type: "varchar(250)",
                maxLength: 250,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Gender",
                table: "Runners",
                type: "varchar(40)",
                maxLength: 40,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "MobileNumber",
                table: "Runners",
                type: "varchar(40)",
                maxLength: 40,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "BibNumber",
                table: "Registrations",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(32)",
                oldMaxLength: 32)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "BibDisplayName",
                table: "Registrations",
                type: "varchar(120)",
                maxLength: 120,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PaymentStatus",
                table: "Registrations",
                type: "varchar(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Unspecified")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Team",
                table: "Registrations",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_EventId_RunnerId",
                table: "Registrations",
                columns: new[] { "EventId", "RunnerId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Requires every registration to have a bib before downgrading.
            migrationBuilder.AlterColumn<string>(
                name: "BibNumber",
                table: "Registrations",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(32)",
                oldMaxLength: 32,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.DropIndex(
                name: "IX_Registrations_EventId_RunnerId",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "Birthdate",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "EmergencyContact",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "Gender",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "MobileNumber",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "BibDisplayName",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "PaymentStatus",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "Team",
                table: "Registrations");


        }
    }
}
