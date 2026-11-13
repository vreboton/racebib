using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Api.Authorization;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Identity;
using RaceTiming.Infrastructure.Persistence;
using RaceTiming.Api.Services;

namespace RaceTiming.Api.Controllers;

[ApiController, Authorize, Route("api/v1/organizers/{organizerId:guid}/members")]
public sealed class OrganizerMembersController(RaceTimingDbContext db, UserManager<ApplicationUser> users, IAuditLogger? audit = null) : ControllerBase
{
    public sealed record MemberDto(Guid MembershipId, string UserId, string? DisplayName, string? Email, OrganizerRole Role, bool IsActive, DateTime JoinedAt);
    public sealed record UserDto(string Id, string? DisplayName, string? Email);
    public sealed record AddMemberRequest([Required, EmailAddress, StringLength(254)] string Email, OrganizerRole Role);
    public sealed record UpdateRoleRequest(OrganizerRole Role);

    [HttpGet]
    public async Task<IActionResult> List(Guid organizerId, [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        if (!await CanAdminister(organizerId, ct)) return Forbid();
        if (page < 1 || pageSize is < 1 or > 100) return BadRequest(new ProblemDetails { Status = 400, Title = "Page must be positive and pageSize must be between 1 and 100." });
        var query = db.OrganizerMemberships.AsNoTracking().Where(x => x.OrganizerId == organizerId)
            .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new { m, u });
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => (x.u.Email ?? "").Contains(term) || (x.u.DisplayName ?? "").Contains(term));
        }
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.u.DisplayName).ThenBy(x => x.u.Email)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new MemberDto(x.m.Id, x.u.Id, x.u.DisplayName, x.u.Email, x.m.Role, x.m.IsActive, x.m.JoinedAt)).ToListAsync(ct);
        return Ok(new { items, page, pageSize, total });
    }

    [HttpGet("user-search")]
    public async Task<IActionResult> SearchUsers(Guid organizerId, [FromQuery] string query, CancellationToken ct)
    {
        if (!await CanAdminister(organizerId, ct)) return Forbid();
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 3) return BadRequest(new ProblemDetails { Status = 400, Title = "Enter at least three characters." });
        var term = query.Trim();
        var existing = db.OrganizerMemberships.AsNoTracking().Where(x => x.OrganizerId == organizerId).Select(x => x.UserId);
        var matches = await db.Users.AsNoTracking().Where(x => x.IsActive && !existing.Contains(x.Id) &&
                ((x.Email ?? "").Contains(term) || (x.DisplayName ?? "").Contains(term)))
            .OrderBy(x => x.Email).Take(20).Select(x => new UserDto(x.Id, x.DisplayName, x.Email)).ToListAsync(ct);
        return Ok(matches);
    }

    [HttpPost]
    public async Task<IActionResult> Add(Guid organizerId, AddMemberRequest request, CancellationToken ct)
    {
        if (!await CanAdminister(organizerId, ct)) return Forbid();
        if (!IsAssignableRole(request.Role)) return BadRequest(new ProblemDetails { Status = 400, Title = "Only EventAdministrator and RaceDirector roles can be assigned." });
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null || !user.IsActive) return NotFound(new ProblemDetails { Status = 404, Title = "No active RaceBib account was found for that email." });
        if (users.GetUserId(User) == user.Id) return BadRequest(new ProblemDetails { Status = 400, Title = "You cannot assign an organizer role to your own account." });
        var membership = await db.OrganizerMemberships.SingleOrDefaultAsync(x => x.OrganizerId == organizerId && x.UserId == user.Id, ct);
        if (membership is { IsActive: true }) return Conflict(new ProblemDetails { Status = 409, Title = "This user is already an active member." });
        var isReactivation = membership is not null;
        var previousRole = membership?.Role;
        if (membership is null)
        {
            membership = new OrganizerMembership { OrganizerId = organizerId, UserId = user.Id, Role = request.Role };
            db.OrganizerMemberships.Add(membership);
        }
        else { membership.Role = request.Role; membership.IsActive = true; membership.JoinedAt = DateTime.UtcNow; }
        audit?.Record(User, isReactivation ? "Organizer.MembershipReactivated" : "Organizer.MemberAdded", "OrganizerMembership", membership.Id.ToString(), organizerId, new Dictionary<string, object?> { ["role"] = membership.Role.ToString() });
        if (isReactivation && previousRole != request.Role)
            audit?.Record(User, "Organizer.MembershipRoleChanged", "OrganizerMembership", membership.Id.ToString(), organizerId,
                new Dictionary<string, object?> { ["from"] = previousRole?.ToString(), ["to"] = request.Role.ToString() });
        await db.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, new MemberDto(membership.Id, user.Id, user.DisplayName, user.Email, membership.Role, true, membership.JoinedAt));
    }

    [HttpPut("{membershipId:guid}/role")]
    public async Task<IActionResult> ChangeRole(Guid organizerId, Guid membershipId, UpdateRoleRequest request, CancellationToken ct)
    {
        if (!await CanAdminister(organizerId, ct)) return Forbid();
        if (!IsAssignableRole(request.Role)) return BadRequest(new ProblemDetails { Status = 400, Title = "Only EventAdministrator and RaceDirector roles can be assigned." });
        var membership = await db.OrganizerMemberships.SingleOrDefaultAsync(x => x.Id == membershipId && x.OrganizerId == organizerId, ct);
        if (membership is null) return NotFound();
        if (users.GetUserId(User) == membership.UserId) return BadRequest(new ProblemDetails { Status = 400, Title = "You cannot change your own organizer role." });
        var oldRole = membership.Role;
        membership.Role = request.Role;
        audit?.Record(User, "Organizer.MembershipRoleChanged", "OrganizerMembership", membership.Id.ToString(), organizerId, new Dictionary<string, object?> { ["from"] = oldRole.ToString(), ["to"] = request.Role.ToString() });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPut("{membershipId:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid organizerId, Guid membershipId, [FromQuery] bool active, CancellationToken ct)
    {
        if (!await CanAdminister(organizerId, ct)) return Forbid();
        var membership = await db.OrganizerMemberships.SingleOrDefaultAsync(x => x.Id == membershipId && x.OrganizerId == organizerId, ct);
        if (membership is null) return NotFound();
        if (users.GetUserId(User) == membership.UserId && !active) return BadRequest(new ProblemDetails { Status = 400, Title = "You cannot deactivate your own membership." });
        membership.IsActive = active;
        audit?.Record(User, active ? "Organizer.MembershipReactivated" : "Organizer.MembershipDeactivated", "OrganizerMembership", membership.Id.ToString(), organizerId);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<bool> CanAdminister(Guid organizerId, CancellationToken ct) =>
        User.IsInRole("SystemAdministrator") ||
        (await db.OrganizerMemberships.AsNoTracking().AnyAsync(x => x.OrganizerId == organizerId && x.UserId == users.GetUserId(User) && x.IsActive && x.Role == OrganizerRole.EventAdministrator && x.Organizer.IsActive && !x.Organizer.IsSuspended, ct));
    private static bool IsAssignableRole(OrganizerRole role) => role is OrganizerRole.EventAdministrator or OrganizerRole.RaceDirector;
}
