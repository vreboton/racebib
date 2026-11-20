using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RaceTiming.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOfficialRunnerDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OfficialRunnerDecisions",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RegistrationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    RaceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Classification = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reason = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    EvidenceFingerprint = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficialRunnerDecisions", x => new { x.EventId, x.RegistrationId, x.Revision });
                    table.ForeignKey(
                        name: "FK_OfficialRunnerDecisions_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OfficialRunnerDecisions_TimingRosterEntries_EventId_Registra~",
                        columns: x => new { x.EventId, x.RegistrationId, x.RaceId },
                        principalTable: "TimingRosterEntries",
                        principalColumns: new[] { "EventId", "RegistrationId", "RaceId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_OfficialRunnerDecisions_ActorUserId",
                table: "OfficialRunnerDecisions",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OfficialRunnerDecisions_EventId_DecidedAtUtc",
                table: "OfficialRunnerDecisions",
                columns: new[] { "EventId", "DecidedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OfficialRunnerDecisions_EventId_RegistrationId_RaceId",
                table: "OfficialRunnerDecisions",
                columns: new[] { "EventId", "RegistrationId", "RaceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OfficialRunnerDecisions");
        }
    }
}
