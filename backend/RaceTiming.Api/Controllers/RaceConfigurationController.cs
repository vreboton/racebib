using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Persistence;
using RaceTiming.Api.Authorization;

namespace RaceTiming.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/events/{eventId:guid}")]
public sealed partial class RaceConfigurationController(
    RaceTimingDbContext db, IOrganizerMembershipAuthorization authorization) : ControllerBase
{
    internal static bool Locked(EventStatus status) => status is
        EventStatus.Ready or EventStatus.Live or EventStatus.Completed or EventStatus.Cancelled;

    private async Task<IActionResult> WithEvent(Guid eventId, bool write, CancellationToken ct,
        Func<Event, Task<IActionResult>> action)
    {
        await using var transaction = write && db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        try
        {
            var item = write ? await FindEventForWrite(db, eventId, ct) :
                await db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == eventId, ct);
            if (item is null) return NotFound();
            if (!await authorization.CanAccessAsync(User, item.OrganizerId, write, ct)) return Forbid();
            if (write && Locked(item.Status)) return ConflictProblem("Race configuration is locked in this event status.");
            var result = await action(item);
            if (transaction is not null && result is not ObjectResult { StatusCode: >= 400 }
                && result is not StatusCodeResult { StatusCode: >= 400 })
                await transaction.CommitAsync(ct);
            return result;
        }
        catch (Exception ex) when (IsWriteConflict(ex))
        {
            return ConflictProblem("A name, code or sequence is already in use, or configuration changed concurrently. Reload and try again.");
        }
    }

    internal static bool IsWriteConflict(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is MySqlException { Number: 1062 or 1205 or 1213 }) return true;
        return false;
    }

    internal static async Task<Event?> FindEventForWrite(RaceTimingDbContext db, Guid id, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return await db.Events.FindAsync([id], ct);

        // Serialize configuration writes and event transitions on the same parent row.
        var events = await db.Events.FromSqlInterpolated($"SELECT * FROM Events WHERE Id = {id} FOR UPDATE")
            .ToListAsync(ct);
        return events.SingleOrDefault();
    }

    private IActionResult Invalid(string message) => BadRequest(new ProblemDetails { Status = 400, Title = message });
    private IActionResult ConflictProblem(string message) => Conflict(new ProblemDetails { Status = 409, Title = message });
    private static bool ValidText(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= max;
    private static bool InWindow(DateTime start, Event item) => start >= item.StartDate && start <= item.EndDate;
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    [HttpGet("configuration")]
    public Task<IActionResult> GetConfiguration(Guid eventId, CancellationToken ct) => WithEvent(eventId, false, ct, async item =>
    {
        var courses = await db.Courses.AsNoTracking().Where(x => x.EventId == eventId)
            .Include(x => x.Checkpoints).OrderBy(x => x.Name).ThenBy(x => x.Version).ToListAsync(ct);
        var races = await db.Races.AsNoTracking().Where(x => x.EventId == eventId)
            .Include(x => x.Categories).Include(x => x.StartWaves).AsSplitQuery().OrderBy(x => x.Name).ToListAsync(ct);
        var activeCourseIds = races.Where(x => x.Status == RecordStatus.Active).Select(x => x.CourseId).ToHashSet();
        var referencedCourseIds = races.Select(x => x.CourseId).ToHashSet();
        var versionedCourseIds = (await CoursesWithRevisionHistory(eventId).ToListAsync(ct)).ToHashSet();
        var historicalCourseIds = (await HistoricalCourses(eventId).ToListAsync(ct)).ToHashSet();
        var history = await db.Registrations.Where(x => x.EventId == eventId)
            .Select(x => new { x.RaceId, x.RaceCategoryId, x.StartWaveId }).Distinct().ToListAsync(ct);
        var registeredRaceIds = history.Select(x => x.RaceId).ToHashSet();
        var registeredCourseIds = races.Where(x => registeredRaceIds.Contains(x.Id)).Select(x => x.CourseId).ToHashSet();
        var categoryIds = history.Select(x => x.RaceCategoryId).ToHashSet();
        var waveIds = history.Select(x => x.StartWaveId).ToHashSet();
        var newerRevisions = (await (
            from race in db.Races
            join course in db.Courses on new { race.EventId, race.Course.Name } equals new { course.EventId, course.Name }
            where race.EventId == eventId && course.Status == RecordStatus.Active && course.Version > race.Course.Version
            select new { RaceId = race.Id, CourseId = course.Id }).ToListAsync(ct))
            .ToLookup(x => x.RaceId, x => x.CourseId);
        return Ok(new
        {
            timeZoneId = TimeZoneInfo.TryConvertWindowsIdToIanaId(item.TimeZoneId, out var zone) ? zone : item.TimeZoneId,
            canEdit = !Locked(item.Status) && await authorization.CanAccessAsync(User, item.OrganizerId, true, ct),
            courses = courses.Select(x => new
            {
                x.Id, x.Name, x.Version, x.Description, x.DistanceKm, x.ElevationGainMeters, x.Status,
                immutable = x.Status != RecordStatus.Active || historicalCourseIds.Contains(x.Id) || registeredCourseIds.Contains(x.Id),
                editBlock = historicalCourseIds.Contains(x.Id) ? "historical" :
                    x.Status != RecordStatus.Active ? "inactive" : registeredCourseIds.Contains(x.Id) ? "registrations" : null,
                hasActiveRaces = activeCourseIds.Contains(x.Id),
                canDelete = !referencedCourseIds.Contains(x.Id) && !versionedCourseIds.Contains(x.Id),
                usable = IsUsable(x),
                checkpoints = x.Checkpoints.OrderBy(c => c.Sequence).Select(c => new
                {
                    c.Id, c.Name, c.Code, c.Sequence, c.DistanceFromStartKm, c.ElevationMeters,
                    c.IsMandatory, c.IsStart, c.IsFinish, c.HasCutoff, c.CutoffDurationSeconds, c.Description
                })
            }),
            races = races.Select(x => RaceDto(x,
                registeredRaceIds.Contains(x.Id) ? [] : newerRevisions[x.Id], categoryIds, waveIds))
        });
    });

    private static object RaceDto(Race x, IEnumerable<Guid> revisionIds,
        HashSet<Guid?> registeredCategoryIds, HashSet<Guid?> registeredWaveIds) => new
    {
        x.Id, x.CourseId, x.Name, x.DistanceKm, x.Description, startAt = Utc(x.StartAtUtc),
        x.CutoffDurationSeconds, x.Status, revisionIds,
        categories = x.Categories.OrderBy(c => c.Name).Select(c => new
            { c.Id, c.Name, c.Description, c.MinimumAge, c.MaximumAge, c.Status, hasRegistrations = registeredCategoryIds.Contains(c.Id) }),
        waves = x.StartWaves.OrderBy(w => w.Sequence).Select(w => new
            { w.Id, w.Name, w.Sequence, startAt = Utc(w.StartAtUtc), w.Capacity, w.Status, hasRegistrations = registeredWaveIds.Contains(w.Id) })
    };

    [HttpGet("races/{raceId:guid}")]
    public Task<IActionResult> GetRace(Guid eventId, Guid raceId, CancellationToken ct) =>
        WithEvent(eventId, false, ct, async _ =>
        {
            var race = await db.Races.AsNoTracking().Include(x => x.Course).Include(x => x.Categories).Include(x => x.StartWaves)
                .AsSplitQuery().SingleOrDefaultAsync(x => x.EventId == eventId && x.Id == raceId, ct);
            if (race is null) return NotFound();
            var history = await db.Registrations.Where(x => x.RaceId == raceId)
                .Select(x => new { x.RaceCategoryId, x.StartWaveId }).Distinct().ToListAsync(ct);
            var revisions = history.Count > 0 ? [] : await db.Courses
                .Where(x => x.EventId == eventId && x.Status == RecordStatus.Active &&
                    x.Name == race.Course.Name && x.Version > race.Course.Version)
                .Select(x => x.Id).ToListAsync(ct);
            return Ok(RaceDto(race, revisions, history.Select(x => x.RaceCategoryId).ToHashSet(),
                history.Select(x => x.StartWaveId).ToHashSet()));
        });
}
