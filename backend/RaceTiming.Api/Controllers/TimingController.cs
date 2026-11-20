using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Api.Services;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Persistence;

namespace RaceTiming.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/events/{eventId:guid}/timing")]
public sealed partial class TimingController(RaceTimingDbContext db, IAuditLogger audit) : ControllerBase
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    private IQueryable<CheckpointAssignment> Grants => db.Set<CheckpointAssignment>().Where(x => x.UserId == Actor && x.Active);
    private IQueryable<Event> EnabledEvents => db.Events.Where(x => x.Organizer.IsActive && !x.Organizer.IsSuspended);

    private Task<bool> ActiveAccount(CancellationToken ct) => db.Users.AnyAsync(x => x.Id == Actor && x.IsActive, ct);

    [HttpGet("/api/v1/me/timing-assignments")]
    public async Task<IActionResult> Assignments(CancellationToken ct)
    {
        if (!await ActiveAccount(ct)) return Forbid();
        var items = await (from grant in Grants
            join item in EnabledEvents on grant.EventId equals item.Id
            join location in db.Set<PhysicalCheckpoint>() on grant.CheckpointId equals location.Id
            orderby item.Name, location.Code
            select new { eventId = item.Id, eventName = item.Name, item.TimeZoneId,
                checkpointId = location.Id, location.Name, location.Code, grant.Generation, entryOpen = item.Status == EventStatus.Live })
            .ToListAsync(ct);
        return Ok(items);
    }

    private async Task<IActionResult> Write(Guid eventId, CancellationToken ct,
        Func<Event, Task<IActionResult>> action, bool manager = true)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var item = await RaceConfigurationController.FindEventForWrite(db, eventId, ct);
            if (item is null) return NotFound();
            if (!await ActiveAccount(ct) || !await EnabledEvents.AnyAsync(x => x.Id == eventId, ct)) return Forbid();
            if (manager && !await db.OrganizerMemberships.AnyAsync(x => x.UserId == Actor &&
                x.OrganizerId == item.OrganizerId && x.IsActive &&
                (x.Role == OrganizerRole.EventAdministrator || x.Role == OrganizerRole.RaceDirector), ct))
                return Forbid();
            var result = await action(item);
            if (result is ObjectResult { StatusCode: >= 400 } or StatusCodeResult { StatusCode: >= 400 }) return result;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (RaceConfigurationController.IsWriteConflict(error))
        {
            return ConflictProblem("timing_write_conflict", "Retry the operation with the same submission identifiers.");
        }
    }

    private ObjectResult ConflictProblem(string code, string message) => Conflict(new ProblemDetails
    {
        Status = 409, Title = message, Extensions = { ["code"] = code }
    });

    private static bool ValidText(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);

    private static DateTime Instant(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        return utc.AddTicks(-(utc.Ticks % 10));
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
