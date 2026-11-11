using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Domain.Services;
using RaceTiming.Api.Authorization;
using RaceTiming.Infrastructure.Persistence;

namespace RaceTiming.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/events")]
public sealed class EventsController(
    RaceTimingDbContext db,
    IOrganizerMembershipAuthorization organizerAuthorization) : ControllerBase
{
    public sealed record CreateEventRequest(
        Guid OrganizerId, string Name, string Slug, string? Description, string OrganizerName,
        string? Venue, string? Location, DateTime StartDateUtc, DateTime EndDateUtc, string TimeZoneId);
    public sealed record UpdateEventRequest(
        string Name, string Slug, string? Description, string OrganizerName,
        string? Venue, string? Location, DateTime StartDateUtc, DateTime EndDateUtc, string TimeZoneId);
    public sealed record ChangeStatusRequest(EventStatus Status);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null, [FromQuery] EventStatus? status = null, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Events.AsNoTracking();
        if (!User.IsInRole("SystemAdministrator"))
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var organizerIds = db.OrganizerMemberships.Where(x => x.UserId == userId && x.IsActive && x.Organizer.IsActive)
                .Select(x => x.OrganizerId);
            query = query.Where(x => organizerIds.Contains(x.OrganizerId));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Name.Contains(term) || x.Location!.Contains(term));
        }
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.StartDate).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.OrganizerId, x.Name, x.Slug, x.Location, x.Venue, x.StartDate, x.EndDate, x.TimeZoneId, x.Status })
            .ToListAsync(ct);
        return Ok(new { items, page, pageSize, total, totalPages = (int)Math.Ceiling(total / (double)pageSize) });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var item = await db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return NotFound();
        if (!await organizerAuthorization.CanAccessAsync(User, item.OrganizerId, false, ct)) return Forbid();
        return Ok(ToDto(item));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateEventRequest request, CancellationToken ct)
    {
        if (!await organizerAuthorization.CanAccessAsync(User, request.OrganizerId, true, ct)) return Forbid();
        var validation = ValidateEvent(request.Name, request.Slug, request.OrganizerName,
            request.StartDateUtc, request.EndDateUtc, request.TimeZoneId);
        if (validation is not null) return BadRequest(validation);
        var slug = request.Slug.Trim().ToLowerInvariant();
        if (await db.Events.AnyAsync(x => x.OrganizerId == request.OrganizerId && x.Slug == slug, ct))
            return Conflict(new ProblemDetails { Status = 409, Title = "Event slug is already in use for this organizer." });

        var item = new Event
        {
            OrganizerId = request.OrganizerId, Name = request.Name.Trim(), Slug = slug,
            Description = request.Description?.Trim(), OrganizerName = request.OrganizerName.Trim(),
            Venue = request.Venue?.Trim(), Location = request.Location?.Trim(),
            StartDate = DateTime.SpecifyKind(request.StartDateUtc, DateTimeKind.Utc),
            EndDate = DateTime.SpecifyKind(request.EndDateUtc, DateTimeKind.Utc),
            TimeZoneId = request.TimeZoneId.Trim(), Status = EventStatus.Draft
        };
        db.Events.Add(item);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = item.Id }, ToDto(item));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateEventRequest request, CancellationToken ct)
    {
        var item = await db.Events.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return NotFound();
        if (!await organizerAuthorization.CanAccessAsync(User, item.OrganizerId, true, ct)) return Forbid();
        if (item.Status is EventStatus.Live or EventStatus.Completed or EventStatus.Cancelled)
            return Conflict(new ProblemDetails { Status = 409, Title = "Event details cannot be edited in its current status." });
        var validation = ValidateEvent(request.Name, request.Slug, request.OrganizerName,
            request.StartDateUtc, request.EndDateUtc, request.TimeZoneId);
        if (validation is not null) return BadRequest(validation);
        var slug = request.Slug.Trim().ToLowerInvariant();
        if (await db.Events.AnyAsync(x => x.Id != id && x.OrganizerId == item.OrganizerId && x.Slug == slug, ct))
            return Conflict(new ProblemDetails { Status = 409, Title = "Event slug is already in use for this organizer." });

        item.Name = request.Name.Trim(); item.Slug = slug; item.Description = request.Description?.Trim();
        item.OrganizerName = request.OrganizerName.Trim(); item.Venue = request.Venue?.Trim();
        item.Location = request.Location?.Trim(); item.StartDate = DateTime.SpecifyKind(request.StartDateUtc, DateTimeKind.Utc);
        item.EndDate = DateTime.SpecifyKind(request.EndDateUtc, DateTimeKind.Utc);
        item.TimeZoneId = request.TimeZoneId.Trim(); item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(item);
    }

    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> ChangeStatus(Guid id, ChangeStatusRequest request, CancellationToken ct)
    {
        var item = await db.Events.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return NotFound();
        if (!await organizerAuthorization.CanAccessAsync(User, item.OrganizerId, true, ct)) return Forbid();
        if (!EventLifecycle.CanTransition(item.Status, request.Status))
            return Conflict(new ProblemDetails { Status = 409, Title = $"Transition from {item.Status} to {request.Status} is not allowed." });
        item.Status = request.Status;
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new { item.Id, item.Status, item.UpdatedAt });
    }

    private static object ToDto(Event item) => new
    {
        item.Id, item.OrganizerId, item.Name, item.Slug, item.Description, item.OrganizerName,
        item.Venue, item.Location, item.StartDate, item.EndDate, item.TimeZoneId,
        item.Status, item.CreatedAt, item.UpdatedAt
    };

    private static ProblemDetails? ValidateEvent(string name, string slug, string organizerName,
        DateTime start, DateTime end, string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200 ||
            string.IsNullOrWhiteSpace(organizerName) || organizerName.Trim().Length > 160)
            return new ProblemDetails { Status = 400, Title = "Event name and organizer name are required and must fit their length limits." };
        if (!System.Text.RegularExpressions.Regex.IsMatch(slug.Trim(), "^[a-zA-Z0-9]+(?:-[a-zA-Z0-9]+)*$") || slug.Length > 220)
            return new ProblemDetails { Status = 400, Title = "Event slug must contain letters, numbers and single hyphens." };
        if (end <= start) return new ProblemDetails { Status = 400, Title = "Event end must be after its start." };
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (TimeZoneNotFoundException) { return new ProblemDetails { Status = 400, Title = "Unknown time zone identifier." }; }
        catch (InvalidTimeZoneException) { return new ProblemDetails { Status = 400, Title = "Invalid time zone identifier." }; }
        return null;
    }
}
