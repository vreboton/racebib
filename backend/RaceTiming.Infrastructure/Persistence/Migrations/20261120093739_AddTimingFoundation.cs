using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RaceTiming.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTimingFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Registrations_Id_EventId",
                table: "Registrations",
                columns: new[] { "Id", "EventId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_CourseCheckpoints_Id_CourseId",
                table: "CourseCheckpoints",
                columns: new[] { "Id", "CourseId" });

            migrationBuilder.CreateTable(
                name: "PhysicalCheckpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    EventId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(160)", maxLength: 160, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Code = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhysicalCheckpoints", x => x.Id);
                    table.UniqueConstraint("AK_PhysicalCheckpoints_Id_EventId", x => new { x.Id, x.EventId });
                    table.ForeignKey(
                        name: "FK_PhysicalCheckpoints_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RaceTimingSetups",
                columns: table => new
                {
                    RaceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    EventId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CourseId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ArmedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    WindowStartUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    WindowEndUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    TimeZoneId = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ScheduledGunUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CutoffDurationSeconds = table.Column<int>(type: "int", nullable: true),
                    ResultsBasis = table.Column<int>(type: "int", nullable: false),
                    CutoffBasis = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaceTimingSetups", x => x.RaceId);
                    table.UniqueConstraint("AK_RaceTimingSetups_RaceId_EventId", x => new { x.RaceId, x.EventId });
                    table.ForeignKey(
                        name: "FK_RaceTimingSetups_Courses_CourseId_EventId",
                        columns: x => new { x.CourseId, x.EventId },
                        principalTable: "Courses",
                        principalColumns: new[] { "Id", "EventId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RaceTimingSetups_Races_RaceId_EventId",
                        columns: x => new { x.RaceId, x.EventId },
                        principalTable: "Races",
                        principalColumns: new[] { "Id", "EventId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CheckpointAssignments",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CheckpointId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Generation = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Active = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckpointAssignments", x => new { x.EventId, x.CheckpointId, x.UserId });
                    table.ForeignKey(
                        name: "FK_CheckpointAssignments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CheckpointAssignments_PhysicalCheckpoints_CheckpointId_Event~",
                        columns: x => new { x.CheckpointId, x.EventId },
                        principalTable: "PhysicalCheckpoints",
                        principalColumns: new[] { "Id", "EventId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CheckpointMappings",
                columns: table => new
                {
                    OccurrenceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CourseId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    EventId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CheckpointId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckpointMappings", x => x.OccurrenceId);
                    table.ForeignKey(
                        name: "FK_CheckpointMappings_CourseCheckpoints_OccurrenceId_CourseId",
                        columns: x => new { x.OccurrenceId, x.CourseId },
                        principalTable: "CourseCheckpoints",
                        principalColumns: new[] { "Id", "CourseId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CheckpointMappings_Courses_CourseId_EventId",
                        columns: x => new { x.CourseId, x.EventId },
                        principalTable: "Courses",
                        principalColumns: new[] { "Id", "EventId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CheckpointMappings_PhysicalCheckpoints_CheckpointId_EventId",
                        columns: x => new { x.CheckpointId, x.EventId },
                        principalTable: "PhysicalCheckpoints",
                        principalColumns: new[] { "Id", "EventId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TimingVisits",
                columns: table => new
                {
                    RaceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    OccurrenceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    EventId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CheckpointId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    DistanceKm = table.Column<decimal>(type: "decimal(8,3)", precision: 8, scale: 3, nullable: false),
                    Name = table.Column<string>(type: "varchar(160)", maxLength: 160, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsStart = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsFinish = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsMandatory = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CutoffDurationSeconds = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimingVisits", x => new { x.RaceId, x.OccurrenceId });
                    table.UniqueConstraint("AK_TimingVisits_RaceId_OccurrenceId_CheckpointId", x => new { x.RaceId, x.OccurrenceId, x.CheckpointId });
                    table.ForeignKey(
                        name: "FK_TimingVisits_CourseCheckpoints_OccurrenceId",
                        column: x => x.OccurrenceId,
                        principalTable: "CourseCheckpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimingVisits_PhysicalCheckpoints_CheckpointId_EventId",
                        columns: x => new { x.CheckpointId, x.EventId },
                        principalTable: "PhysicalCheckpoints",
                        principalColumns: new[] { "Id", "EventId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimingVisits_RaceTimingSetups_RaceId_EventId",
                        columns: x => new { x.RaceId, x.EventId },
                        principalTable: "RaceTimingSetups",
                        principalColumns: new[] { "RaceId", "EventId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TimingWaves",
                columns: table => new
                {
                    RaceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    WaveId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ScheduledGunUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimingWaves", x => new { x.RaceId, x.WaveId });
                    table.ForeignKey(
                        name: "FK_TimingWaves_RaceTimingSetups_RaceId",
                        column: x => x.RaceId,
                        principalTable: "RaceTimingSetups",
                        principalColumn: "RaceId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimingWaves_StartWaves_WaveId_RaceId",
                        columns: x => new { x.WaveId, x.RaceId },
                        principalTable: "StartWaves",
                        principalColumns: new[] { "Id", "RaceId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "OfficialGunDeclarations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RaceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    WaveId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    GunAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    DeclaredAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ActorUserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reason = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SupersedesId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficialGunDeclarations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OfficialGunDeclarations_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OfficialGunDeclarations_OfficialGunDeclarations_SupersedesId",
                        column: x => x.SupersedesId,
                        principalTable: "OfficialGunDeclarations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OfficialGunDeclarations_RaceTimingSetups_RaceId",
                        column: x => x.RaceId,
                        principalTable: "RaceTimingSetups",
                        principalColumn: "RaceId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OfficialGunDeclarations_TimingWaves_RaceId_WaveId",
                        columns: x => new { x.RaceId, x.WaveId },
                        principalTable: "TimingWaves",
                        principalColumns: new[] { "RaceId", "WaveId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TimingRosterEntries",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RegistrationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RaceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    WaveId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    BibNumber = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimingRosterEntries", x => new { x.EventId, x.RegistrationId });
                    table.UniqueConstraint("AK_TimingRosterEntries_EventId_RegistrationId_RaceId", x => new { x.EventId, x.RegistrationId, x.RaceId });
                    table.ForeignKey(
                        name: "FK_TimingRosterEntries_RaceTimingSetups_RaceId_EventId",
                        columns: x => new { x.RaceId, x.EventId },
                        principalTable: "RaceTimingSetups",
                        principalColumns: new[] { "RaceId", "EventId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimingRosterEntries_Registrations_RegistrationId_EventId",
                        columns: x => new { x.RegistrationId, x.EventId },
                        principalTable: "Registrations",
                        principalColumns: new[] { "Id", "EventId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimingRosterEntries_TimingWaves_RaceId_WaveId",
                        columns: x => new { x.RaceId, x.WaveId },
                        principalTable: "TimingWaves",
                        principalColumns: new[] { "RaceId", "WaveId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TimingObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    EventId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RaceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RegistrationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CheckpointId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ProposedOccurrenceId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    BibNumber = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ObservedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ActorUserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DeviceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    IdempotencyKey = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AssignmentGeneration = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Source = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimingObservations", x => x.Id);
                    table.UniqueConstraint("AK_TimingObservations_Id_EventId_RegistrationId_RaceId", x => new { x.Id, x.EventId, x.RegistrationId, x.RaceId });
                    table.ForeignKey(
                        name: "FK_TimingObservations_CheckpointAssignments_EventId_CheckpointI~",
                        columns: x => new { x.EventId, x.CheckpointId, x.ActorUserId },
                        principalTable: "CheckpointAssignments",
                        principalColumns: new[] { "EventId", "CheckpointId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimingObservations_TimingRosterEntries_EventId_RegistrationI~",
                        columns: x => new { x.EventId, x.RegistrationId, x.RaceId },
                        principalTable: "TimingRosterEntries",
                        principalColumns: new[] { "EventId", "RegistrationId", "RaceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimingObservations_TimingVisits_RaceId_ProposedOccurrenceId_~",
                        columns: x => new { x.RaceId, x.ProposedOccurrenceId, x.CheckpointId },
                        principalTable: "TimingVisits",
                        principalColumns: new[] { "RaceId", "OccurrenceId", "CheckpointId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TimingDecisions",
                columns: table => new
                {
                    ObservationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    OccurrenceId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ActorUserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reason = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimingDecisions", x => new { x.ObservationId, x.Revision });
                    table.ForeignKey(
                        name: "FK_TimingDecisions_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimingDecisions_CourseCheckpoints_OccurrenceId",
                        column: x => x.OccurrenceId,
                        principalTable: "CourseCheckpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimingDecisions_TimingObservations_ObservationId",
                        column: x => x.ObservationId,
                        principalTable: "TimingObservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AcceptedTimings",
                columns: table => new
                {
                    RegistrationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    OccurrenceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    EventId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RaceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ObservationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    DecisionRevision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcceptedTimings", x => new { x.RegistrationId, x.OccurrenceId });
                    table.ForeignKey(
                        name: "FK_AcceptedTimings_TimingDecisions_ObservationId_DecisionRevisi~",
                        columns: x => new { x.ObservationId, x.DecisionRevision },
                        principalTable: "TimingDecisions",
                        principalColumns: new[] { "ObservationId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AcceptedTimings_TimingObservations_ObservationId_EventId_Reg~",
                        columns: x => new { x.ObservationId, x.EventId, x.RegistrationId, x.RaceId },
                        principalTable: "TimingObservations",
                        principalColumns: new[] { "Id", "EventId", "RegistrationId", "RaceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AcceptedTimings_TimingVisits_RaceId_OccurrenceId",
                        columns: x => new { x.RaceId, x.OccurrenceId },
                        principalTable: "TimingVisits",
                        principalColumns: new[] { "RaceId", "OccurrenceId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AcceptedTimings_EventId_RaceId_OccurrenceId",
                table: "AcceptedTimings",
                columns: new[] { "EventId", "RaceId", "OccurrenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_AcceptedTimings_ObservationId",
                table: "AcceptedTimings",
                column: "ObservationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcceptedTimings_ObservationId_DecisionRevision",
                table: "AcceptedTimings",
                columns: new[] { "ObservationId", "DecisionRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_AcceptedTimings_ObservationId_EventId_RegistrationId_RaceId",
                table: "AcceptedTimings",
                columns: new[] { "ObservationId", "EventId", "RegistrationId", "RaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_AcceptedTimings_RaceId_OccurrenceId",
                table: "AcceptedTimings",
                columns: new[] { "RaceId", "OccurrenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_CheckpointAssignments_CheckpointId_EventId",
                table: "CheckpointAssignments",
                columns: new[] { "CheckpointId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_CheckpointAssignments_UserId_Active",
                table: "CheckpointAssignments",
                columns: new[] { "UserId", "Active" });

            migrationBuilder.CreateIndex(
                name: "IX_CheckpointMappings_CheckpointId_EventId",
                table: "CheckpointMappings",
                columns: new[] { "CheckpointId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_CheckpointMappings_CourseId_EventId",
                table: "CheckpointMappings",
                columns: new[] { "CourseId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_CheckpointMappings_OccurrenceId_CourseId",
                table: "CheckpointMappings",
                columns: new[] { "OccurrenceId", "CourseId" });

            migrationBuilder.CreateIndex(
                name: "IX_OfficialGunDeclarations_ActorUserId",
                table: "OfficialGunDeclarations",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OfficialGunDeclarations_RaceId_WaveId_DeclaredAtUtc",
                table: "OfficialGunDeclarations",
                columns: new[] { "RaceId", "WaveId", "DeclaredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OfficialGunDeclarations_SupersedesId",
                table: "OfficialGunDeclarations",
                column: "SupersedesId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalCheckpoints_EventId_Code",
                table: "PhysicalCheckpoints",
                columns: new[] { "EventId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RaceTimingSetups_CourseId_EventId",
                table: "RaceTimingSetups",
                columns: new[] { "CourseId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_TimingDecisions_ActorUserId",
                table: "TimingDecisions",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TimingDecisions_OccurrenceId",
                table: "TimingDecisions",
                column: "OccurrenceId");

            migrationBuilder.CreateIndex(
                name: "IX_TimingObservations_EventId_CheckpointId_ActorUserId",
                table: "TimingObservations",
                columns: new[] { "EventId", "CheckpointId", "ActorUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_TimingObservations_EventId_CheckpointId_ReceivedAtUtc",
                table: "TimingObservations",
                columns: new[] { "EventId", "CheckpointId", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TimingObservations_EventId_RegistrationId_ObservedAtUtc",
                table: "TimingObservations",
                columns: new[] { "EventId", "RegistrationId", "ObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TimingObservations_EventId_RegistrationId_RaceId",
                table: "TimingObservations",
                columns: new[] { "EventId", "RegistrationId", "RaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_TimingObservations_RaceId_ProposedOccurrenceId_CheckpointId",
                table: "TimingObservations",
                columns: new[] { "RaceId", "ProposedOccurrenceId", "CheckpointId" });

            migrationBuilder.CreateIndex(
                name: "UX_TimingObservations_Operation",
                table: "TimingObservations",
                columns: new[] { "EventId", "ActorUserId", "DeviceId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimingRosterEntries_EventId_BibNumber",
                table: "TimingRosterEntries",
                columns: new[] { "EventId", "BibNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimingRosterEntries_RaceId_EventId",
                table: "TimingRosterEntries",
                columns: new[] { "RaceId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_TimingRosterEntries_RaceId_WaveId",
                table: "TimingRosterEntries",
                columns: new[] { "RaceId", "WaveId" });

            migrationBuilder.CreateIndex(
                name: "IX_TimingRosterEntries_RegistrationId_EventId",
                table: "TimingRosterEntries",
                columns: new[] { "RegistrationId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_TimingVisits_CheckpointId_EventId",
                table: "TimingVisits",
                columns: new[] { "CheckpointId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_TimingVisits_OccurrenceId",
                table: "TimingVisits",
                column: "OccurrenceId");

            migrationBuilder.CreateIndex(
                name: "IX_TimingVisits_RaceId_CheckpointId",
                table: "TimingVisits",
                columns: new[] { "RaceId", "CheckpointId" });

            migrationBuilder.CreateIndex(
                name: "IX_TimingVisits_RaceId_EventId",
                table: "TimingVisits",
                columns: new[] { "RaceId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_TimingVisits_RaceId_Sequence",
                table: "TimingVisits",
                columns: new[] { "RaceId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimingWaves_WaveId_RaceId",
                table: "TimingWaves",
                columns: new[] { "WaveId", "RaceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AcceptedTimings");

            migrationBuilder.DropTable(
                name: "CheckpointMappings");

            migrationBuilder.DropTable(
                name: "OfficialGunDeclarations");

            migrationBuilder.DropTable(
                name: "TimingDecisions");

            migrationBuilder.DropTable(
                name: "TimingObservations");

            migrationBuilder.DropTable(
                name: "CheckpointAssignments");

            migrationBuilder.DropTable(
                name: "TimingRosterEntries");

            migrationBuilder.DropTable(
                name: "TimingVisits");

            migrationBuilder.DropTable(
                name: "TimingWaves");

            migrationBuilder.DropTable(
                name: "PhysicalCheckpoints");

            migrationBuilder.DropTable(
                name: "RaceTimingSetups");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Registrations_Id_EventId",
                table: "Registrations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CourseCheckpoints_Id_CourseId",
                table: "CourseCheckpoints");
        }
    }
}
