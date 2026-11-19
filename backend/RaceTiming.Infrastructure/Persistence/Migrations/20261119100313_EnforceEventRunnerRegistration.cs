using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RaceTiming.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceEventRunnerRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EventRunnerUniquenessSlot",
                table: "Registrations",
                type: "tinyint(1)",
                nullable: true,
                defaultValue: true);

            // Keep one indexed slot per existing pair and grandfather any additional historical rows.
            migrationBuilder.Sql("""
                UPDATE Registrations AS duplicate
                INNER JOIN Registrations AS retained
                  ON retained.EventId = duplicate.EventId
                 AND retained.RunnerId = duplicate.RunnerId
                 AND retained.Id < duplicate.Id
                SET duplicate.EventRunnerUniquenessSlot = NULL
                """);

            migrationBuilder.CreateIndex(
                name: "UX_Registrations_Event_Runner_Slot",
                table: "Registrations",
                columns: new[] { "EventId", "RunnerId", "EventRunnerUniquenessSlot" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Registrations_EventRunnerSlot",
                table: "Registrations",
                sql: "EventRunnerUniquenessSlot IS NULL OR EventRunnerUniquenessSlot = 1");

            migrationBuilder.DropIndex(
                name: "IX_Registrations_EventId_RunnerId",
                table: "Registrations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Registrations_Event_Runner_Slot",
                table: "Registrations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Registrations_EventRunnerSlot",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "EventRunnerUniquenessSlot",
                table: "Registrations");

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_EventId_RunnerId",
                table: "Registrations",
                columns: new[] { "EventId", "RunnerId" });
        }
    }
}
