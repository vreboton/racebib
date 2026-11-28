using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Domain.Services;

namespace RaceTiming.Api.Controllers;

public sealed partial class TimingController
{
    [HttpGet("/api/v1/me/timing-clock")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> TimingClock(CancellationToken ct)
    {
        if (!await ActiveAccount(ct)) return Forbid();
        return Ok(new { serverUtc = DateTimeOffset.UtcNow });
    }

    [HttpGet("setup")]
    public Task<IActionResult> SetupOverview(Guid eventId, CancellationToken ct) =>
        ReadRaceControl(eventId, ct, async () =>
        {
            var item = await db.Events.Where(x => x.Id == eventId)
                .Select(x => new { x.StartDate, x.EndDate, x.TimeZoneId }).SingleAsync(ct);
            var now = DateTime.UtcNow;
            var races = await db.Races.Where(x => x.EventId == eventId && (x.Status == RecordStatus.Active ||
                db.Set<RaceTimingSetup>().Any(setup => setup.RaceId == x.Id)))
                .OrderBy(x => x.Name).Select(x => new
                {
                    x.Id, x.Name, x.CourseId, x.StartAtUtc,
                    waves = x.StartWaves.OrderBy(wave => wave.Sequence).Select(wave => new { wave.Id, wave.Name })
                }).ToListAsync(ct);
            var courses = await db.Courses.Where(x => x.EventId == eventId && x.Races.Any(r => r.Status == RecordStatus.Active))
                .OrderBy(x => x.Name).Select(x => new
                {
                    x.Id, x.Name, x.Version,
                    occurrences = x.Checkpoints.OrderBy(visit => visit.Sequence)
                        .Select(visit => new { visit.Id, visit.Name, visit.Sequence })
                }).ToListAsync(ct);
            var locations = await db.Set<PhysicalCheckpoint>().Where(x => x.EventId == eventId)
                .OrderBy(x => x.Code).Select(x => new { x.Id, x.Name, x.Code }).ToListAsync(ct);
            var mappings = await db.Set<CheckpointMapping>().Where(x => x.EventId == eventId)
                .Select(x => new { x.OccurrenceId, x.CheckpointId }).ToListAsync(ct);
            var setups = await db.Set<RaceTimingSetup>().Where(x => x.EventId == eventId)
                .Select(x => new { x.RaceId, x.CourseId, x.ResultsBasis, x.CutoffBasis, x.WindowStartUtc, x.WindowEndUtc, x.ScheduledGunUtc, x.ArmedAtUtc }).ToListAsync(ct);
            var assignments = await (from grant in db.Set<CheckpointAssignment>()
                join user in db.Users on grant.UserId equals user.Id
                where grant.EventId == eventId && grant.Active
                orderby user.DisplayName, grant.CheckpointId
                select new { grant.CheckpointId, grant.UserId, user.DisplayName, user.Email, grant.Generation, grant.OfflineHours })
                .ToListAsync(ct);
            var allGuns = db.Set<OfficialGunDeclaration>();
            var guns = await (from gun in allGuns
                join setup in db.Set<RaceTimingSetup>() on gun.RaceId equals setup.RaceId
                where setup.EventId == eventId && !allGuns.Any(next => next.SupersedesId == gun.Id)
                select new { gun.Id, gun.RaceId, gun.WaveId, gun.GunAtUtc }).ToListAsync(ct);
            return Ok(new
            {
                eventSchedule = new { startUtc = item.StartDate, endUtc = item.EndDate,
                    timeZoneId = EventsController.BrowserTimeZone(item.TimeZoneId) },
                races, courses, locations, mappings, assignments,
                setups = setups.Select(x => new
                {
                    x.RaceId, x.CourseId, x.ResultsBasis, x.CutoffBasis, x.WindowStartUtc, x.WindowEndUtc,
                    x.ScheduledGunUtc, x.ArmedAtUtc,
                    windowStatus = TimingWindowPolicy.Status(x.WindowStartUtc, x.WindowEndUtc, now),
                    differsFromEventSchedule = x.WindowStartUtc != item.StartDate || x.WindowEndUtc != item.EndDate
                }),
                guns = guns.Select(x => new { x.Id, x.RaceId, x.WaveId, gunAtUtc = Utc(x.GunAtUtc) })
            });
        });

    [HttpGet("setup/operator")]
    public Task<IActionResult> FindCheckpointOperator(Guid eventId, string email, CancellationToken ct) =>
        ReadRaceControl(eventId, ct, async () =>
        {
            if (!ValidText(email, 254)) return BadRequest("Enter the operator's exact account email.");
            var normalized = email.Trim().Normalize().ToUpperInvariant();
            var account = await db.Users.Where(x => x.IsActive && x.NormalizedEmail == normalized)
                .Select(x => new { x.Id, x.Email, x.DisplayName }).SingleOrDefaultAsync(ct);
            return account is null ? NotFound() : Ok(account);
        });

    [HttpGet("setup/races/{raceId:guid}/late-registrations")]
    public Task<IActionResult> LateTimingRegistrations(Guid eventId, Guid raceId, string bib, CancellationToken ct) =>
        ReadResults(eventId, raceId, ct, async _ =>
        {
            if (!ValidText(bib, 32)) return BadRequest("Enter the exact bib number.");
            var limits = db.Set<TimingRosterEntry>().Where(x => x.EventId == eventId);
            var row = await EligibleRoster(eventId, raceId).Where(x => x.BibNumber == bib.Trim() &&
                !limits.Any(frozen => frozen.RegistrationId == x.Id || frozen.BibNumber == x.BibNumber))
                .Select(x => new { x.Id, x.BibNumber, x.Runner.FirstName, x.Runner.LastName }).SingleOrDefaultAsync(ct);
            return row is null ? NotFound() : Ok(row);
        });
}
