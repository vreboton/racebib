using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Identity;
using RaceTiming.Infrastructure.Persistence;

namespace RaceTiming.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/organizers")]
public sealed class OrganizersController(
    RaceTimingDbContext db,
    UserManager<ApplicationUser> users) : ControllerBase
{
    public sealed record CreateOrganizerRequest(string Name, string Slug, string? Description);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (User.IsInRole("SystemAdministrator"))
            return Ok(await db.Organizers.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct));

        var userId = users.GetUserId(User)!;
        var items = await db.OrganizerMemberships.AsNoTracking()
            .Where(x => x.UserId == userId && x.IsActive && x.Organizer.IsActive)
            .Select(x => x.Organizer)
            .OrderBy(x => x.Name).ToListAsync(ct);
        return Ok(items);
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateOrganizerRequest request, CancellationToken ct)
    {
        var name = request.Name.Trim();
        var slug = request.Slug.Trim().ToLowerInvariant();
        if (name.Length is < 2 or > 160 || slug.Length is < 2 or > 180 ||
            !System.Text.RegularExpressions.Regex.IsMatch(slug, "^[a-z0-9]+(?:-[a-z0-9]+)*$"))
            return BadRequest(new ProblemDetails { Status = 400, Title = "Organizer name or slug is invalid." });

        if (await db.Organizers.AnyAsync(x => x.Slug == slug, ct))
            return Conflict(new ProblemDetails { Status = 409, Title = "Organizer slug is already in use." });

        var organizer = new Organizer { Name = name, Slug = slug, Description = request.Description?.Trim() };
        var userId = users.GetUserId(User)!;
        db.Organizers.Add(organizer);
        db.OrganizerMemberships.Add(new OrganizerMembership
        {
            OrganizerId = organizer.Id, UserId = userId, Role = OrganizerRole.EventAdministrator
        });
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(List), new { id = organizer.Id }, new
        {
            organizer.Id, organizer.Name, organizer.Slug, organizer.Description
        });
    }
}
