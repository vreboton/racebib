using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Persistence;

namespace RaceTiming.Api.Controllers;

[ApiController]
[Route("api/v1/public/events/{eventId:guid}/participants")]
public sealed class PublicParticipantsController(RaceTimingDbContext db) : ControllerBase
{
    public sealed record Participant(string NameOnBib, string? BibNumber, string Race);

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> List(Guid eventId, int page = 1, CancellationToken ct = default)
    {
        var published = await db.Events.AnyAsync(x => x.Id == eventId
            && x.Organizer.IsActive && !x.Organizer.IsSuspended
            && x.Status != EventStatus.Draft && x.Status != EventStatus.Cancelled, ct);
        if (!published) return NotFound();
        var rows = await db.Registrations.AsNoTracking()
            .Where(x => x.EventId == eventId && x.RegistrationStatus == RegistrationStatus.Confirmed
                && x.Event.Status != EventStatus.Draft && x.Event.Status != EventStatus.Cancelled
                && x.Event.Organizer.IsActive && !x.Event.Organizer.IsSuspended)
            .OrderBy(x => x.BibNumber).ThenBy(x => x.Id)
            .Skip((Math.Clamp(page, 1, 100_000) - 1) * 100).Take(100)
            .Select(x => new Participant(x.BibDisplayName == null || x.BibDisplayName == ""
                ? x.Runner.FirstName + " " + x.Runner.LastName : x.BibDisplayName, x.BibNumber, x.Race.Name))
            .ToListAsync(ct);
        return Ok(rows);
    }
}
