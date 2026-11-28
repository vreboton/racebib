using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RaceTiming.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOfflineStationPermitPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OfflineHours",
                table: "CheckpointAssignments",
                type: "int",
                nullable: false,
                defaultValue: 12);

            migrationBuilder.AddColumn<Guid>(
                name: "OfflinePolicyRevision",
                table: "CheckpointAssignments",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OfflineHours",
                table: "CheckpointAssignments");

            migrationBuilder.DropColumn(
                name: "OfflinePolicyRevision",
                table: "CheckpointAssignments");
        }
    }
}
