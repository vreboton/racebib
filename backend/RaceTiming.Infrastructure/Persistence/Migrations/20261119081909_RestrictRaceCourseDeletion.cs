using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RaceTiming.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RestrictRaceCourseDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Races_Courses_CourseId_EventId",
                table: "Races");

            migrationBuilder.AddForeignKey(
                name: "FK_Races_Courses_CourseId_EventId",
                table: "Races",
                columns: new[] { "CourseId", "EventId" },
                principalTable: "Courses",
                principalColumns: new[] { "Id", "EventId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Races_Courses_CourseId_EventId",
                table: "Races");

            migrationBuilder.AddForeignKey(
                name: "FK_Races_Courses_CourseId_EventId",
                table: "Races",
                columns: new[] { "CourseId", "EventId" },
                principalTable: "Courses",
                principalColumns: new[] { "Id", "EventId" },
                onDelete: ReferentialAction.Cascade);
        }
    }
}
