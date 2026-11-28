using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Domain.Services;

namespace RaceTiming.Api.Controllers;

public sealed partial class TimingController
{
    public sealed record LocationInput(string Name, string Code);
    public sealed record MappingInput(Guid CheckpointId);
    public sealed record AssignmentInput(bool Active);
    public sealed record ArmInput(TimingBasis ResultsBasis = TimingBasis.Gun, TimingBasis CutoffBasis = TimingBasis.Gun);
    public sealed record RosterInput(string Reason);
    public sealed record GunInput(Guid? WaveId, DateTimeOffset GunAtUtc, Guid? ExpectedDeclarationId, string Reason);

    [HttpPost("checkpoints")]
    public Task<IActionResult> CreateCheckpoint(Guid eventId, LocationInput input, CancellationToken ct) =>
        Write(eventId, ct, item =>
        {
            if (!ValidText(input.Name, 160) || !ValidText(input.Code, 40))
                return Task.FromResult<IActionResult>(BadRequest("Provide a checkpoint name and code."));
            var location = new PhysicalCheckpoint { EventId = eventId, Name = input.Name.Trim(), Code = input.Code.Trim() };
            db.Add(location);
            audit.Record(User, "Timing.CheckpointCreated", nameof(PhysicalCheckpoint), location.Id.ToString(), item.OrganizerId);
            return Task.FromResult<IActionResult>(StatusCode(201, location));
        });

    [HttpPut("courses/{courseId:guid}/occurrences/{occurrenceId:guid}/checkpoint")]
    public Task<IActionResult> MapCheckpoint(Guid eventId, Guid courseId, Guid occurrenceId, MappingInput input, CancellationToken ct) =>
        Write(eventId, ct, async item =>
        {
            if (await db.Set<RaceTimingSetup>().AnyAsync(x => x.EventId == eventId && x.CourseId == courseId, ct))
                return ConflictProblem("course_armed", "The course mapping is frozen for timing.");
            if (!await db.CourseCheckpoints.AnyAsync(x => x.Id == occurrenceId && x.CourseId == courseId && x.Course.EventId == eventId, ct) ||
                !await db.Set<PhysicalCheckpoint>().AnyAsync(x => x.Id == input.CheckpointId && x.EventId == eventId, ct))
                return NotFound();
            var mapping = await db.Set<CheckpointMapping>().FindAsync([occurrenceId], ct);
            if (mapping is null)
                db.Add(new CheckpointMapping { OccurrenceId = occurrenceId, CourseId = courseId, EventId = eventId, CheckpointId = input.CheckpointId });
            else
                mapping.CheckpointId = input.CheckpointId;
            audit.Record(User, "Timing.CheckpointMapped", nameof(CourseCheckpoint), occurrenceId.ToString(), item.OrganizerId);
            return NoContent();
        });

    [HttpPut("checkpoints/{checkpointId:guid}/assignments/{userId}")]
    public Task<IActionResult> SetAssignment(Guid eventId, Guid checkpointId, string userId, AssignmentInput input, CancellationToken ct) =>
        Write(eventId, ct, async item =>
        {
            if (!await db.Set<PhysicalCheckpoint>().AnyAsync(x => x.EventId == eventId && x.Id == checkpointId, ct) ||
                (input.Active && !await db.Users.AnyAsync(x => x.Id == userId && x.IsActive, ct)))
                return NotFound();
            if (input.Active && item.Status == EventStatus.Completed)
                return ConflictProblem("event_completed", "Checkpoint access cannot be granted for a completed event.");

            var grant = await db.Set<CheckpointAssignment>().FindAsync([eventId, checkpointId, userId], ct);
            if (grant is null)
            {
                if (!input.Active) return NoContent();
                grant = new CheckpointAssignment { EventId = eventId, CheckpointId = checkpointId, UserId = userId };
                db.Add(grant);
            }
            if (grant.Active != input.Active)
            {
                grant.Active = input.Active;
                grant.Generation = Guid.NewGuid();
                grant.OfflineHours = 12;
                grant.OfflinePolicyRevision = Guid.Empty;
                audit.Record(User, input.Active ? "Timing.AssignmentGranted" : "Timing.AssignmentRevoked",
                    nameof(CheckpointAssignment), grant.Generation.ToString(), item.OrganizerId);
            }
            return Ok(new { grant.Active, grant.Generation });
        });

    [HttpPost("races/{raceId:guid}/arm")]
    public Task<IActionResult> Arm(Guid eventId, Guid raceId, ArmInput input, CancellationToken ct) =>
        Write(eventId, ct, async item =>
        {
            if (!Enum.IsDefined(input.ResultsBasis) || !Enum.IsDefined(input.CutoffBasis)) return BadRequest("Unknown timing basis.");
            if (item.Status is not (EventStatus.Ready or EventStatus.Live))
                return ConflictProblem("event_not_ready", "Timing can be armed when the event is ready or live.");
            var race = await db.Races.Include(x => x.StartWaves).Include(x => x.Course).ThenInclude(x => x.Checkpoints)
                .SingleOrDefaultAsync(x => x.EventId == eventId && x.Id == raceId && x.Status == RecordStatus.Active, ct);
            if (race is null) return NotFound();
            if (await db.Set<RaceTimingSetup>().AnyAsync(x => x.RaceId == raceId, ct))
                return ConflictProblem("already_armed", "The race is already armed.");
            var armedAt = DateTime.UtcNow;
            if (item.StartDate >= item.EndDate)
                return ConflictProblem("invalid_timing_window", "Set the event end after its start before arming timing.");
            if (armedAt > item.EndDate)
                return ConflictProblem("expired_timing_window", "The event timing window has ended. Update the event schedule before arming.");
            if (!TimingWindowPolicy.Contains(item.StartDate, item.EndDate, race.StartAtUtc))
                return ConflictProblem("race_outside_timing_window", "Move the scheduled race start inside the event timing window before arming.");
            var activeWaves = race.StartWaves.Where(wave => wave.Status == RecordStatus.Active).ToArray();
            var invalidWave = activeWaves.OrderBy(wave => wave.Sequence).FirstOrDefault(wave =>
                !TimingWindowPolicy.Contains(race.StartAtUtc, item.EndDate, wave.StartAtUtc));
            if (invalidWave is not null)
                return ConflictProblem("wave_outside_timing_window",
                    $"Move start wave '{invalidWave.Name}' to a time between the race start and event end before arming.");
            if (!EventsController.HasValidWaveSchedule(race, item.EndDate))
                return ConflictProblem("invalid_timing_wave_schedule", "Check active wave order, start times, and capacity before arming.");
            var mappings = await db.Set<CheckpointMapping>().Where(x => x.CourseId == race.CourseId)
                .ToDictionaryAsync(x => x.OccurrenceId, x => x.CheckpointId, ct);
            var points = race.Course.Checkpoints;
            if (points.Count < 2 || points.Count(x => x.IsStart) != 1 || points.Count(x => x.IsFinish) != 1 ||
                !points.MinBy(x => x.Sequence)!.IsStart || !points.MaxBy(x => x.Sequence)!.IsFinish ||
                points.Any(x => !mappings.ContainsKey(x.Id)))
                return ConflictProblem("incomplete_route", "Map every visit and configure a start and finish before arming.");
            var setup = new RaceTimingSetup
            {
                EventId = eventId, RaceId = raceId, CourseId = race.CourseId, ArmedAtUtc = armedAt,
                WindowStartUtc = item.StartDate, WindowEndUtc = item.EndDate, TimeZoneId = item.TimeZoneId,
                ScheduledGunUtc = race.StartAtUtc, CutoffDurationSeconds = race.CutoffDurationSeconds,
                ResultsBasis = input.ResultsBasis, CutoffBasis = input.CutoffBasis
            };
            db.Add(setup);
            db.AddRange(points.Select(x => new TimingVisit
            {
                EventId = eventId, RaceId = raceId, OccurrenceId = x.Id, CheckpointId = mappings[x.Id],
                Sequence = x.Sequence, Name = x.Name, DistanceKm = x.DistanceFromStartKm, IsStart = x.IsStart,
                IsFinish = x.IsFinish, IsMandatory = x.IsMandatory,
                CutoffDurationSeconds = x.HasCutoff ? x.CutoffDurationSeconds : null
            }));
            db.AddRange(race.StartWaves.Select(x => new TimingWave { RaceId = raceId, WaveId = x.Id, ScheduledGunUtc = x.StartAtUtc }));
            var roster = await EligibleRoster(eventId, raceId).ToListAsync(ct);
            db.AddRange(roster.Select(RosterEntry));
            audit.Record(User, "Timing.Armed", nameof(RaceTimingSetup), raceId.ToString(), item.OrganizerId);
            return StatusCode(201, new { raceId, setup.CourseId, rosterCount = roster.Count });
        });

    [HttpPost("races/{raceId:guid}/roster/{registrationId:guid}")]
    public Task<IActionResult> AmendRoster(Guid eventId, Guid raceId, Guid registrationId, RosterInput input, CancellationToken ct) =>
        Write(eventId, ct, async item =>
        {
            if (!ValidText(input.Reason, 300)) return BadRequest("Provide an amendment reason.");
            if (item.Status is not (EventStatus.Ready or EventStatus.Live)) return ConflictProblem("roster_closed", "The timing roster is closed.");
            if (!await db.Set<RaceTimingSetup>().AnyAsync(x => x.RaceId == raceId && x.EventId == eventId, ct)) return NotFound();
            var entry = await EligibleRoster(eventId, raceId).SingleOrDefaultAsync(x => x.Id == registrationId, ct);
            if (entry is null) return NotFound();
            if (await db.Set<TimingRosterEntry>().AnyAsync(x => x.EventId == eventId &&
                (x.RegistrationId == registrationId || x.BibNumber == entry.BibNumber), ct))
                return ConflictProblem("roster_identity_reserved", "The registration or bib is already reserved by timing.");
            if (entry.StartWaveId is Guid waveId && !await db.Set<TimingWave>().AnyAsync(x => x.RaceId == raceId && x.WaveId == waveId, ct))
                return ConflictProblem("wave_not_frozen", "The start wave was unavailable when timing was armed.");
            db.Add(RosterEntry(entry));
            audit.Record(User, "Timing.RosterAmended", nameof(TimingRosterEntry), registrationId.ToString(), item.OrganizerId,
                new Dictionary<string, object?> { ["reason"] = input.Reason.Trim() });
            return NoContent();
        });

    [HttpPost("races/{raceId:guid}/gun-starts")]
    public Task<IActionResult> DeclareGun(Guid eventId, Guid raceId, GunInput input, CancellationToken ct) =>
        Write(eventId, ct, async item =>
        {
            if (!ValidText(input.Reason, 300) || input.GunAtUtc == default) return BadRequest("Provide a gun instant and reason.");
            if (item.Status is not (EventStatus.Live or EventStatus.Completed))
                return ConflictProblem("gun_declaration_closed", "Gun declarations require a live or completed event.");
            if (!await db.Set<RaceTimingSetup>().AnyAsync(x => x.RaceId == raceId && x.EventId == eventId, ct) ||
                (input.WaveId is Guid waveId && !await db.Set<TimingWave>().AnyAsync(x => x.RaceId == raceId && x.WaveId == waveId, ct)))
                return NotFound();
            var prior = await db.Set<OfficialGunDeclaration>().Where(x => x.RaceId == raceId && x.WaveId == input.WaveId &&
                !db.Set<OfficialGunDeclaration>().Any(next => next.SupersedesId == x.Id)).SingleOrDefaultAsync(ct);
            if (prior?.Id != input.ExpectedDeclarationId)
                return ConflictProblem("stale_gun_declaration", "Reload the current gun declaration before replacing it.");
            var declaration = new OfficialGunDeclaration
            {
                RaceId = raceId, WaveId = input.WaveId, GunAtUtc = Instant(input.GunAtUtc), DeclaredAtUtc = DateTime.UtcNow,
                ActorUserId = Actor, Reason = input.Reason.Trim(), SupersedesId = prior?.Id
            };
            db.Add(declaration);
            audit.Record(User, "Timing.GunDeclared", nameof(OfficialGunDeclaration), declaration.Id.ToString(), item.OrganizerId);
            return StatusCode(201, new { declaration.Id, declaration.WaveId, gunAtUtc = Utc(declaration.GunAtUtc), declaration.SupersedesId });
        });

    private IQueryable<Registration> EligibleRoster(Guid eventId, Guid raceId) => db.Registrations.AsNoTracking()
        .Where(x => x.EventId == eventId && x.RaceId == raceId && x.RegistrationStatus == RegistrationStatus.Confirmed && x.BibNumber != null);

    private static TimingRosterEntry RosterEntry(Registration registration) => new()
    {
        EventId = registration.EventId, RegistrationId = registration.Id, RaceId = registration.RaceId,
        WaveId = registration.StartWaveId, BibNumber = registration.BibNumber!
    };
}
