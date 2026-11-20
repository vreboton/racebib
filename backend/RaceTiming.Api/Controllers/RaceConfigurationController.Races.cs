using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;

namespace RaceTiming.Api.Controllers;

public sealed partial class RaceConfigurationController
{
    public sealed record RaceInput(Guid CourseId, string Name, string? Description, string StartAt,
        int? CutoffDurationSeconds, bool EnsureRegistrationCategory = false);
    public sealed record CategoryInput(string Name, string? Description, int? MinimumAge, int? MaximumAge);
    public sealed record WaveInput(string Name, int Sequence, string StartAt, int? Capacity);

    [HttpPost("races")]
    public Task<IActionResult> CreateRace(Guid eventId, RaceInput input, CancellationToken ct) => SaveRace(eventId, null, input, ct);

    [HttpPut("races/{raceId:guid}")]
    public Task<IActionResult> UpdateRace(Guid eventId, Guid raceId, RaceInput input, CancellationToken ct) =>
        SaveRace(eventId, raceId, input, ct);

    private Task<IActionResult> SaveRace(Guid eventId, Guid? raceId, RaceInput input, CancellationToken ct) =>
        WithEvent(eventId, true, ct, async item =>
        {
            if (!ValidText(input.Name, 120) || input.CutoffDurationSeconds <= 0 ||
                !TryStart(input.StartAt, out var start) || !InWindow(start, item))
                return Invalid("Provide a race name, an offset-aware start within the event window, and a positive cutoff if set.");
            var race = raceId is null ? new Race { EventId = eventId, Name = input.Name.Trim() }
                : await db.Races.Include(x => x.StartWaves).Include(x => x.Course)
                    .SingleOrDefaultAsync(x => x.Id == raceId && x.EventId == eventId, ct);
            if (race is null) return NotFound();
            if (race.Status != RecordStatus.Active) return ConflictProblem("This race is inactive.");
            var course = await FindCourse(eventId, input.CourseId, ct);
            if (course is null) return NotFound();
            if (!IsUsable(course)) return Invalid("Select an active course with valid start and finish checkpoints.");
            if (raceId.HasValue && race.CourseId != course.Id)
            {
                if (!CanSwitchCourse(item, race.Status))
                    return ConflictProblem("A race can switch course revisions only while its event is in Draft.");
                if (await db.Registrations.AnyAsync(x => x.RaceId == race.Id, ct))
                    return ConflictProblem("A race with registrations cannot switch revisions here. Edit its course while the event is in Draft.");
                if (course.Version <= race.Course.Version ||
                    !await db.Courses.AnyAsync(x => x.Id == course.Id && x.Name == race.Course.Name, ct))
                    return Invalid("Switch to a newer revision of this race's course.");
            }
            if (race.StartWaves.Any(x => x.Status == RecordStatus.Active && x.StartAtUtc < start))
                return Invalid("The race start cannot be after an active start wave.");
            race.CourseId = course.Id;
            race.DistanceKm = course.DistanceKm;
            race.Name = input.Name.Trim();
            race.Description = input.Description?.Trim();
            race.StartAtUtc = start;
            race.CutoffDurationSeconds = input.CutoffDurationSeconds;
            if (!raceId.HasValue) db.Races.Add(race);
            if (input.EnsureRegistrationCategory && (!raceId.HasValue ||
                !await db.RaceCategories.AnyAsync(x => x.RaceId == race.Id && x.Status == RecordStatus.Active, ct)))
            {
                var plan = await RegistrationsController.PlanManagedCategory(db, race.Id, ct);
                await RegistrationsController.ApplyManagedCategory(db, race.Id, plan, ct);
            }
            await db.SaveChangesAsync(ct);
            return Ok(new { race.Id });
        });

    [HttpDelete("races/{raceId:guid}")]
    public Task<IActionResult> RetireRace(Guid eventId, Guid raceId, CancellationToken ct) =>
        WithEvent(eventId, true, ct, async _ =>
        {
            var race = await FindRace(eventId, raceId, ct);
            if (race is null) return NotFound();
            race.Status = RecordStatus.Inactive;
            await db.SaveChangesAsync(ct);
            return NoContent();
        });

    [HttpPost("races/{raceId:guid}/categories")]
    public Task<IActionResult> CreateCategory(Guid eventId, Guid raceId, CategoryInput input, CancellationToken ct) =>
        SaveCategory(eventId, raceId, null, input, ct);

    [HttpPut("races/{raceId:guid}/categories/{categoryId:guid}")]
    public Task<IActionResult> UpdateCategory(Guid eventId, Guid raceId, Guid categoryId, CategoryInput input, CancellationToken ct) =>
        SaveCategory(eventId, raceId, categoryId, input, ct);

    private Task<IActionResult> SaveCategory(Guid eventId, Guid raceId, Guid? categoryId, CategoryInput input, CancellationToken ct) =>
        WithActiveRace(eventId, raceId, ct, async (_, _) =>
        {
            if (!ValidText(input.Name, 120) || input.MinimumAge < 0 || input.MaximumAge < 0 || input.MinimumAge > input.MaximumAge)
                return Invalid("Provide an Age Group name and nonnegative ages with minimum no greater than maximum.");
            var category = categoryId is null ? new RaceCategory { RaceId = raceId, Name = input.Name.Trim() }
                : await db.RaceCategories.SingleOrDefaultAsync(x => x.RaceId == raceId && x.Id == categoryId, ct);
            if (category is null) return NotFound();
            if (category.Status != RecordStatus.Active) return ConflictProblem("This Age Group has been retired.");
            if (categoryId.HasValue && (category.Name != input.Name.Trim() ||
                category.Description != input.Description?.Trim() || category.MinimumAge != input.MinimumAge ||
                category.MaximumAge != input.MaximumAge) &&
                await db.Registrations.AnyAsync(x => x.RaceCategoryId == category.Id, ct))
                return ConflictProblem("An Age Group used by registrations cannot be edited. Retire it and create a replacement.");
            category.Name = input.Name.Trim();
            category.Description = input.Description?.Trim();
            category.MinimumAge = input.MinimumAge;
            category.MaximumAge = input.MaximumAge;
            if (!categoryId.HasValue) db.RaceCategories.Add(category);
            await db.SaveChangesAsync(ct);
            return Ok(new { category.Id });
        });

    [HttpDelete("races/{raceId:guid}/categories/{categoryId:guid}")]
    public Task<IActionResult> RetireCategory(Guid eventId, Guid raceId, Guid categoryId, CancellationToken ct) =>
        WithActiveRace(eventId, raceId, ct, async (_, _) =>
        {
            var category = await db.RaceCategories.SingleOrDefaultAsync(x => x.RaceId == raceId && x.Id == categoryId, ct);
            if (category is null) return NotFound();
            category.Status = RecordStatus.Inactive;
            await db.SaveChangesAsync(ct);
            return NoContent();
        });

    [HttpPost("races/{raceId:guid}/waves")]
    public Task<IActionResult> CreateWave(Guid eventId, Guid raceId, WaveInput input, CancellationToken ct) =>
        SaveWave(eventId, raceId, null, input, ct);

    [HttpPut("races/{raceId:guid}/waves/{waveId:guid}")]
    public Task<IActionResult> UpdateWave(Guid eventId, Guid raceId, Guid waveId, WaveInput input, CancellationToken ct) =>
        SaveWave(eventId, raceId, waveId, input, ct);

    private Task<IActionResult> SaveWave(Guid eventId, Guid raceId, Guid? waveId, WaveInput input, CancellationToken ct) =>
        WithActiveRace(eventId, raceId, ct, async (item, race) =>
        {
            if (!ValidText(input.Name, 120) || input.Sequence <= 0 || input.Capacity <= 0 ||
                !TryStart(input.StartAt, out var start) || !InWindow(start, item) || start < race.StartAtUtc)
                return Invalid("Provide a Start Wave name, positive sequence and optional capacity, and a start within the event at or after the Category start.");
            var wave = waveId is null ? new StartWave { RaceId = raceId, Name = input.Name.Trim() }
                : await db.StartWaves.SingleOrDefaultAsync(x => x.RaceId == raceId && x.Id == waveId, ct);
            if (wave is null) return NotFound();
            if (wave.Status != RecordStatus.Active) return ConflictProblem("This Start Wave has been retired.");
            if (waveId.HasValue && (wave.Name != input.Name.Trim() || wave.Sequence != input.Sequence ||
                wave.StartAtUtc != start || wave.Capacity != input.Capacity) &&
                await db.Registrations.AnyAsync(x => x.StartWaveId == wave.Id, ct))
                return ConflictProblem("A Start Wave used by registrations cannot be edited. Retire it and create a replacement.");
            var otherWaves = db.StartWaves.Where(x => x.RaceId == raceId && x.Id != wave.Id);
            if (await otherWaves.AnyAsync(x => x.Sequence == input.Sequence, ct))
                return ConflictProblem("This Start Wave sequence is already in use.");
            if (await otherWaves.AnyAsync(x => x.Status == RecordStatus.Active &&
                ((x.Sequence < input.Sequence && x.StartAtUtc > start) ||
                 (x.Sequence > input.Sequence && x.StartAtUtc < start)), ct))
                return Invalid("Active Start Wave start times must be in sequence order.");
            wave.Name = input.Name.Trim();
            wave.Sequence = input.Sequence;
            wave.StartAtUtc = start;
            wave.Capacity = input.Capacity;
            if (!waveId.HasValue) db.StartWaves.Add(wave);
            await db.SaveChangesAsync(ct);
            return Ok(new { wave.Id });
        });

    [HttpDelete("races/{raceId:guid}/waves/{waveId:guid}")]
    public Task<IActionResult> RetireWave(Guid eventId, Guid raceId, Guid waveId, CancellationToken ct) =>
        WithActiveRace(eventId, raceId, ct, async (_, _) =>
        {
            var wave = await db.StartWaves.SingleOrDefaultAsync(x => x.RaceId == raceId && x.Id == waveId, ct);
            if (wave is null) return NotFound();
            wave.Status = RecordStatus.Inactive;
            await db.SaveChangesAsync(ct);
            return NoContent();
        });

    private Task<Race?> FindRace(Guid eventId, Guid raceId, CancellationToken ct) =>
        db.Races.SingleOrDefaultAsync(x => x.EventId == eventId && x.Id == raceId, ct);

    private static bool CanSwitchCourse(Event item, RecordStatus status) =>
        item.Status == EventStatus.Draft && status == RecordStatus.Active;

    private Task<IActionResult> WithActiveRace(Guid eventId, Guid raceId, CancellationToken ct,
        Func<Event, Race, Task<IActionResult>> action) =>
        WithEvent(eventId, true, ct, async item =>
        {
            var race = await FindRace(eventId, raceId, ct);
            if (race is null) return NotFound();
            if (race.Status != RecordStatus.Active) return ConflictProblem("This Category has been retired.");
            return await action(item, race);
        });

    private static bool TryStart(string? value, out DateTime start)
    {
        start = default;
        if (value is null || !Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,6})?)?(Z|[+-]\d{2}:\d{2})$") ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        start = parsed.UtcDateTime;
        return true;
    }
}
