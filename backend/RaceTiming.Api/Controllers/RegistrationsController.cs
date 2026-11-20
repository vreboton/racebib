using System.Data;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Api.Authorization;
using RaceTiming.Api.Services;
using RaceTiming.Application;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Persistence;

namespace RaceTiming.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/events/{eventId:guid}/registrations")]
public sealed partial class RegistrationsController(
    RaceTimingDbContext db, IOrganizerMembershipAuthorization authorization, IAuditLogger audit) : ControllerBase
{
    public sealed record RegistrationInput(Guid RaceId, Guid RunnerId, Guid? CategoryId, Guid? WaveId,
        string? BibDisplayName, string? BibNumber, string? Team, RegistrationStatus Status,
        PaymentStatus PaymentStatus, BibAssignment? Assignment);

    [HttpGet]
    public Task<IActionResult> List(Guid eventId, string? search, Guid? raceId, RegistrationStatus? status,
        int page = 1, CancellationToken ct = default) => WithEvent(eventId, false, ct, async _ =>
    {
        var query = Entries(eventId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Runner.FirstName.Contains(term) || x.Runner.LastName.Contains(term)
                || (x.BibDisplayName != null && x.BibDisplayName.Contains(term))
                || (x.BibNumber != null && x.BibNumber.Contains(term)));
        }
        if (raceId.HasValue) query = query.Where(x => x.RaceId == raceId);
        if (status.HasValue) query = query.Where(x => x.RegistrationStatus == status);
        var count = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.Runner.LastName).ThenBy(x => x.Id)
            .Skip((Math.Clamp(page, 1, 100_000) - 1) * 50).Take(50).ToListAsync(ct);
        return Ok(new { total = count, items = items.Select(Admin) });
    });

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Get(Guid eventId, Guid id, CancellationToken ct) =>
        WithEvent(eventId, false, ct, async _ =>
        {
            var entry = await Entries(eventId).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (entry is null) return NotFound();
            var history = await db.AuditRecords.Where(x => x.OrganizerId == entry.OrganizerId
                    && x.TargetEntityType == "Registration" && x.TargetEntityId == id.ToString())
                .OrderByDescending(x => x.TimestampUtc)
                .Select(x => new { x.Action, x.TimestampUtc, x.ActorUserId, x.MetadataJson })
                .ToListAsync(ct);
            return Ok(new { registration = Admin(entry), history });
        });

    [HttpPost]
    public Task<IActionResult> Create(Guid eventId, RegistrationInput input, CancellationToken ct) =>
        WithEvent(eventId, true, ct, item => Save(item, null, input, ct));

    [HttpPut("{id:guid}")]
    public Task<IActionResult> Update(Guid eventId, Guid id, RegistrationInput input, CancellationToken ct) =>
        WithEvent(eventId, true, ct, item => Save(item, id, input, ct));

    [HttpPost("{id:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid eventId, Guid id, CancellationToken ct) =>
        WithEvent(eventId, true, ct, async _ =>
        {
            var entry = await db.Registrations.SingleOrDefaultAsync(x => x.EventId == eventId && x.Id == id, ct);
            if (entry is null) return NotFound();
            if (entry.RegistrationStatus == RegistrationStatus.Cancelled) return NoContent();
            entry.RegistrationStatus = RegistrationStatus.Cancelled;
            Record(entry, "Registration.Cancelled");
            await db.SaveChangesAsync(ct);
            return NoContent();
        });

    public sealed record BibInput(string? BibNumber, BibAssignment? Assignment);

    [HttpPut("{id:guid}/bib")]
    public Task<IActionResult> ChangeBib(Guid eventId, Guid id, BibInput input, CancellationToken ct) =>
        WithEvent(eventId, true, ct, async _ =>
        {
            var entry = await db.Registrations.SingleOrDefaultAsync(x => x.EventId == eventId && x.Id == id, ct);
            if (entry is null) return NotFound();
            if (entry.RegistrationStatus == RegistrationStatus.Cancelled)
                return Conflict("Reopen the registration before changing its reserved bib.");
            var occupied = await db.Registrations.Where(x => x.EventId == eventId && x.Id != id)
                .Select(x => x.BibNumber).ToListAsync(ct);
            var error = BibAllocator.Allocate(input.Assignment ?? new(), occupied, [input.BibNumber], out var bibs);
            if (error is not null) return Conflict(error);
            if (entry.BibNumber == bibs[0]) return NoContent();
            entry.BibNumber = bibs[0];
            Record(entry, "Registration.BibChanged");
            await db.SaveChangesAsync(ct);
            return NoContent();
        });

    private async Task<IActionResult> Save(Event item, Guid? id, RegistrationInput input, CancellationToken ct)
    {
        var entry = id.HasValue
            ? await db.Registrations.SingleOrDefaultAsync(x => x.EventId == item.Id && x.Id == id, ct)
            : null;
        if (id.HasValue && entry is null) return NotFound();
        if (entry is not null && entry.RunnerId != input.RunnerId)
            return BadRequest("The runner on an existing registration is fixed.");
        await RunnerProfileWriteLock.Acquire(db, item.OrganizerId, ct);
        var runner = await db.Runners.SingleOrDefaultAsync(x => x.Id == input.RunnerId
            && x.OrganizerId == item.OrganizerId, ct);
        if (runner is null) return BadRequest("Select a runner from this organizer.");
        if (entry is null && await db.Registrations.AnyAsync(x => x.EventId == item.Id && x.RunnerId == runner.Id, ct))
            return Conflict("This runner already has a registration for this event.");

        var race = await RacesWithRegistrationOptions(item.Id).SingleOrDefaultAsync(x => x.Id == input.RaceId, ct);
        var error = Validate(item, race, runner, input, entry);
        if (error is not null) return BadRequest(error);
        var previousBib = entry?.BibNumber;
        var assignment = input.Assignment ?? new();
        var selectedBib = previousBib;
        if (entry is null || assignment.Mode != BibAssignmentMode.Existing ||
            !string.Equals(input.BibNumber, previousBib, StringComparison.Ordinal))
        {
            var occupied = await db.Registrations.Where(x => x.EventId == item.Id && x.Id != id)
                .Select(x => x.BibNumber).ToListAsync(ct);
            error = BibAllocator.Allocate(assignment, occupied, [input.BibNumber], out var bibs);
            if (error is not null) return Conflict(error);
            selectedBib = bibs[0];
        }
        if (entry is not null && input.Status == RegistrationStatus.Cancelled && previousBib != selectedBib)
            return Conflict("Cancellation must keep the existing bib reserved.");
        entry ??= new Registration { EventId = item.Id, OrganizerId = item.OrganizerId, Runner = runner };
        entry.Runner = runner;
        Apply(entry, input, selectedBib);
        if (!id.HasValue) db.Registrations.Add(entry);
        Record(entry, id.HasValue ? "Registration.Updated" : "Registration.Created");
        if (previousBib != entry.BibNumber) Record(entry, "Registration.BibChanged");
        await db.SaveChangesAsync(ct);
        return id.HasValue ? Ok(Admin(entry)) : CreatedAtAction(nameof(Get), new { eventId = item.Id, id = entry.Id }, Admin(entry));
    }

    private IQueryable<Race> RacesWithRegistrationOptions(Guid eventId) => db.Races.AsNoTracking().AsSplitQuery()
        .Include(x => x.Categories).Include(x => x.StartWaves).Where(x => x.EventId == eventId);

    private static string? Validate(Event item, Race? race, Runner runner, RegistrationInput input, Registration? existing)
    {
        if (!Enum.IsDefined(input.Status) || !Enum.IsDefined(input.PaymentStatus))
            return "Select valid registration and payment statuses.";
        if (input.BibDisplayName?.Trim().Length > 120 ||
            (input.BibDisplayName is string name && (new StringInfo(name.Trim()).LengthInTextElements > 60 || name.Any(char.IsControl))))
            return "Name on Bib must be at most 60 printed characters without control characters.";
        if (input.Team?.Trim().Length > 100) return "Team must be at most 100 characters.";
        return ValidateAssignments(item, race, runner, input.CategoryId, input.WaveId, existing);
    }

    internal static string? ValidateAssignments(Event item, Race? race, Runner runner, Guid? ageGroupId,
        Guid? startWaveId, Registration? existing)
    {
        if (race is null || (race.Status != RecordStatus.Active && existing?.RaceId != race.Id))
            return "Select an active Race Category from this event.";
        if (ageGroupId is Guid categoryId)
        {
            var category = race.Categories.FirstOrDefault(x => x.Id == categoryId);
            if (category is null || (category.Status != RecordStatus.Active && existing?.RaceCategoryId != categoryId))
                return "Select an active Age Group from this Race Category.";
            if (category.MinimumAge.HasValue || category.MaximumAge.HasValue)
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(item.TimeZoneId);
                var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(race.StartAtUtc, DateTimeKind.Utc), zone));
                if (runner.Birthdate is not DateOnly birth || birth > date || birth > DateOnly.FromDateTime(DateTime.UtcNow))
                    return "This Age Group requires a valid birthdate.";
                var age = date.Year - birth.Year;
                if (birth.AddYears(age) > date) age--;
                if (age < category.MinimumAge || age > category.MaximumAge)
                    return "The runner is outside the Age Group limits.";
            }
        }
        var allowedWave = existing?.StartWaveId;
        if (startWaveId is Guid waveId && !race.StartWaves.Any(x => x.Id == waveId
            && (x.Status == RecordStatus.Active || allowedWave == x.Id)))
            return "Select an active Start Wave from this Race Category.";
        return null;
    }

    private static void Apply(Registration entry, RegistrationInput input, string? bib)
    {
        entry.RaceId = input.RaceId;
        entry.RaceCategoryId = input.CategoryId;
        entry.StartWaveId = input.WaveId;
        entry.BibNumber = bib;
        entry.BibDisplayName = Optional(input.BibDisplayName);
        entry.Team = Optional(input.Team);
        entry.RegistrationStatus = input.Status;
        entry.PaymentStatus = input.PaymentStatus;
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private IQueryable<Registration> Entries(Guid eventId) => db.Registrations.AsNoTracking()
        .Include(x => x.Runner).Where(x => x.EventId == eventId);

    private static object Admin(Registration x) => new
    {
        x.Id, x.RunnerId, x.RaceId, categoryId = x.RaceCategoryId, waveId = x.StartWaveId,
        x.BibNumber, x.BibDisplayName, x.EffectiveBibDisplayName, x.Team,
        status = (int)x.RegistrationStatus, paymentStatus = (int)x.PaymentStatus,
        runner = new { x.Runner.FirstName, x.Runner.LastName, x.Runner.Email, x.Runner.Gender,
            x.Runner.Birthdate, x.Runner.MobileNumber, x.Runner.EmergencyContact }
    };

    private void Record(Registration entry, string action) =>
        audit.Record(User, action, "Registration", entry.Id.ToString(), entry.OrganizerId);

    private async Task<IActionResult> WithEvent(Guid eventId, bool write, CancellationToken ct, Func<Event, Task<IActionResult>> action)
    {
        var scope = await db.Events.AsNoTracking().Where(x => x.Id == eventId)
            .Select(x => new { x.OrganizerId }).SingleOrDefaultAsync(ct);
        if (scope is null) return NotFound();
        if (!await authorization.CanAccessAsync(User, scope.OrganizerId, true, ct)) return Forbid();
        await using var transaction = write && db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct) : null;
        try
        {
            var item = write ? await RaceConfigurationController.FindEventForWrite(db, eventId, ct)
                : await db.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == eventId, ct);
            if (item is null) return NotFound();
            if (write && item.Status is EventStatus.Completed or EventStatus.Cancelled)
                return Conflict("Registration changes are closed for this event.");
            var result = await action(item);
            if (transaction is not null && result is not ObjectResult { StatusCode: >= 400 }
                && result is not StatusCodeResult { StatusCode: >= 400 })
                await transaction.CommitAsync(ct);
            return result;
        }
        catch (Exception ex) when (RaceConfigurationController.IsWriteConflict(ex))
        {
            return Conflict("A registration or bib changed concurrently. Reload and try again.");
        }
    }
}
