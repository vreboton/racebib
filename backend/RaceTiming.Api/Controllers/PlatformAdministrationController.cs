using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Api.Authorization;
using RaceTiming.Api.Services;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Identity;
using RaceTiming.Infrastructure.Persistence;

namespace RaceTiming.Api.Controllers;

[ApiController, Authorize(Roles = AppRoles.SystemAdministrator), Route("api/v1/platform")]
public sealed class PlatformAdministrationController(
    RaceTimingDbContext db, UserManager<ApplicationUser> users, RoleManager<IdentityRole> roles,
    IAuditLogger audit) : ControllerBase
{
    public sealed record SetPlatformAdminRequest(bool IsAdministrator);
    public sealed record SetAccountStatusRequest(bool IsActive);
    public sealed record SetOrganizerStatusRequest(string Status);
    public sealed record UserRow(string Id, string? DisplayName, string? Email, bool IsActive, bool IsSystemAdministrator, DateTimeOffset? LockoutEnd);
    public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int TotalCount);

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken ct)
    {
        var usersCount = await users.Users.CountAsync(ct);
        var activeOrganizers = await db.Organizers.CountAsync(x => x.IsActive && !x.IsSuspended, ct);
        var suspended = await db.Organizers.CountAsync(x => x.IsActive && x.IsSuspended, ct);
        var inactive = await db.Organizers.CountAsync(x => !x.IsActive, ct);
        var platformAdmins = await users.GetUsersInRoleAsync(AppRoles.SystemAdministrator);
        var recent = await db.AuditRecords.AsNoTracking().OrderByDescending(x => x.TimestampUtc).Take(10)
            .Select(x => new { x.Id, x.Action, x.TargetEntityType, x.TargetEntityId, x.TimestampUtc }).ToListAsync(ct);
        return Ok(new { RegisteredUsers = usersCount, ActiveOrganizers = activeOrganizers, SuspendedOrganizers = suspended,
            InactiveOrganizers = inactive, PlatformAdministrators = platformAdmins.Count(u => u.IsActive), RecentActivity = recent });
    }

    [HttpGet("users")]
    public async Task<IActionResult> ListUsers([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        if (page < 1 || pageSize is < 1 or > 100) return BadRequest(new ProblemDetails { Status = 400, Title = "Page must be positive and pageSize must be between 1 and 100." });
        var query = users.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => (x.Email ?? "").Contains(term) || (x.DisplayName ?? "").Contains(term));
        }
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.Email).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.Email, x.DisplayName, x.IsActive, x.LockoutEnd }).ToListAsync(ct);
        var adminIds = (await users.GetUsersInRoleAsync(AppRoles.SystemAdministrator)).Select(x => x.Id).ToHashSet();
        return Ok(new Page<UserRow>(rows.Select(x => new UserRow(x.Id, x.DisplayName, x.Email, x.IsActive, adminIds.Contains(x.Id), x.LockoutEnd)).ToList(), page, pageSize, total));
    }

    [HttpGet("users/{userId}")]
    public async Task<IActionResult> UserDetails(string userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null) return NotFound();
        var memberships = await db.OrganizerMemberships.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => new { x.OrganizerId, OrganizerName = x.Organizer.Name, x.Role, x.IsActive, x.JoinedAt }).ToListAsync(ct);
        return Ok(new { user.Id, user.Email, user.DisplayName, user.IsActive, user.LockoutEnd,
            PlatformRoles = await users.GetRolesAsync(user), OrganizerMemberships = memberships });
    }

    [HttpPut("users/{userId}/platform-admin")]
    public async Task<IActionResult> SetPlatformAdmin(string userId, SetPlatformAdminRequest request, CancellationToken ct)
    {
        var target = await users.FindByIdAsync(userId);
        if (target is null) return NotFound();
        if (users.GetUserId(User) == userId && request.IsAdministrator && !User.IsInRole(AppRoles.SystemAdministrator)) return Forbid();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!await roles.RoleExistsAsync(AppRoles.SystemAdministrator))
        {
            var roleCreate = await roles.CreateAsync(new IdentityRole(AppRoles.SystemAdministrator));
            if (!roleCreate.Succeeded) return StatusCode(500, new ProblemDetails { Status = 500, Title = "Platform role could not be initialized." });
        }
        var currentlyAdmin = await users.IsInRoleAsync(target, AppRoles.SystemAdministrator);
        if (currentlyAdmin == request.IsAdministrator) return NoContent();
        if (!request.IsAdministrator && target.IsActive)
        {
            var admins = await users.GetUsersInRoleAsync(AppRoles.SystemAdministrator);
            if (admins.Count(u => u.IsActive) <= 1)
                return Conflict(new ProblemDetails { Status = 409, Title = "The last active System Administrator cannot be removed." });
        }
        var result = request.IsAdministrator
            ? await users.AddToRoleAsync(target, AppRoles.SystemAdministrator)
            : await users.RemoveFromRoleAsync(target, AppRoles.SystemAdministrator);
        if (!result.Succeeded) return BadRequest(new { errors = result.Errors.Select(x => x.Description) });
        audit.Record(User, request.IsAdministrator ? "Platform.AdministratorAssigned" : "Platform.AdministratorRevoked", "User", target.Id,
            metadata: null);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return NoContent();
    }

    [HttpPut("users/{userId}/status")]
    public async Task<IActionResult> SetUserStatus(string userId, SetAccountStatusRequest request, CancellationToken ct)
    {
        var target = await users.FindByIdAsync(userId);
        if (target is null) return NotFound();
        if (users.GetUserId(User) == userId && !request.IsActive) return BadRequest(new ProblemDetails { Status = 400, Title = "You cannot deactivate your own account." });
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!request.IsActive && target.IsActive && await users.IsInRoleAsync(target, AppRoles.SystemAdministrator))
        {
            var admins = await users.GetUsersInRoleAsync(AppRoles.SystemAdministrator);
            if (admins.Count(u => u.IsActive) <= 1) return Conflict(new ProblemDetails { Status = 409, Title = "The last active System Administrator cannot be deactivated." });
        }
        target.IsActive = request.IsActive;
        audit.Record(User, request.IsActive ? "Platform.UserReactivated" : "Platform.UserDeactivated", "User", target.Id,
            metadata: null);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return NoContent();
    }

    [HttpGet("organizers")]
    public async Task<IActionResult> ListOrganizers([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        if (page < 1 || pageSize is < 1 or > 100) return BadRequest(new ProblemDetails { Status = 400, Title = "Page must be positive and pageSize must be between 1 and 100." });
        var query = db.Organizers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim(); query = query.Where(x => x.Name.Contains(term) || x.Slug.Contains(term)); }
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.Name).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.Name, x.Slug, x.Description, x.IsActive, x.IsSuspended, x.CreatedAt,
                MemberCount = x.Memberships.Count(m => m.IsActive), EventCount = x.Events.Count })
            .ToListAsync(ct);
        return Ok(new Page<object>(rows.Select(x => (object)new { x.Id, x.Name, x.Slug, x.Description, Status = StatusOf(x.IsActive, x.IsSuspended), x.CreatedAt, x.MemberCount, x.EventCount }).ToList(), page, pageSize, total));
    }

    [HttpGet("organizers/{organizerId:guid}")]
    public async Task<IActionResult> OrganizerDetails(Guid organizerId, CancellationToken ct)
    {
        var x = await db.Organizers.AsNoTracking().Where(o => o.Id == organizerId).Select(o => new
        {
            o.Id, o.Name, o.Slug, o.Description, o.IsActive, o.IsSuspended, o.CreatedAt,
            Members = o.Memberships.Count(m => m.IsActive), Events = o.Events.Count,
            Registrations = db.Registrations.Count(r => r.OrganizerId == o.Id)
        }).SingleOrDefaultAsync(ct);
        return x is null ? NotFound() : Ok(new { x.Id, x.Name, x.Slug, x.Description, Status = StatusOf(x.IsActive, x.IsSuspended), x.CreatedAt, MemberCount = x.Members, EventCount = x.Events, RegistrationCount = x.Registrations });
    }

    [HttpGet("organizers/{organizerId:guid}/members")]
    public async Task<IActionResult> OrganizerMembers(Guid organizerId, CancellationToken ct)
    {
        if (!await db.Organizers.AnyAsync(x => x.Id == organizerId, ct)) return NotFound();
        var rows = await db.OrganizerMemberships.AsNoTracking().Where(x => x.OrganizerId == organizerId)
            .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new { u.Id, u.DisplayName, u.Email, m.Role, m.IsActive, m.JoinedAt }).ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("organizers/{organizerId:guid}/events")]
    public async Task<IActionResult> OrganizerEvents(Guid organizerId, CancellationToken ct)
    {
        if (!await db.Organizers.AnyAsync(x => x.Id == organizerId, ct)) return NotFound();
        return Ok(await db.Events.AsNoTracking().Where(x => x.OrganizerId == organizerId).OrderByDescending(x => x.StartDate)
            .Select(x => new { x.Id, x.Name, x.Slug, x.Status, x.StartDate, x.EndDate, RegistrationCount = db.Registrations.Count(r => r.EventId == x.Id) }).ToListAsync(ct));
    }

    [HttpPut("organizers/{organizerId:guid}/status")]
    public async Task<IActionResult> SetOrganizerStatus(Guid organizerId, SetOrganizerStatusRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<OrganizerPlatformStatus>(request.Status, true, out var status) || !Enum.IsDefined(status))
            return BadRequest(new ProblemDetails { Status = 400, Title = "Status must be Active, Suspended or Inactive." });
        var organizer = await db.Organizers.SingleOrDefaultAsync(x => x.Id == organizerId, ct);
        if (organizer is null) return NotFound();
        var old = StatusOf(organizer.IsActive, organizer.IsSuspended);
        organizer.IsActive = status != OrganizerPlatformStatus.Inactive;
        organizer.IsSuspended = status == OrganizerPlatformStatus.Suspended;
        if (old != status.ToString())
        {
            audit.Record(User, status switch { OrganizerPlatformStatus.Suspended => "Platform.OrganizerSuspended", OrganizerPlatformStatus.Active when old == "Suspended" => "Platform.OrganizerReactivated", OrganizerPlatformStatus.Active => "Platform.OrganizerActivated", _ => "Platform.OrganizerDeactivated" }, "Organizer", organizer.Id.ToString(), organizer.Id,
                new Dictionary<string, object?> { ["from"] = old, ["to"] = status.ToString() });
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    [HttpGet("audit")]
    public async Task<IActionResult> PlatformAudit([FromQuery] string? actorId, [FromQuery] string? action,
        [FromQuery] string? entityType, [FromQuery] string? search, [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
        => await QueryAudit(null, actorId, action, entityType, search, fromUtc, toUtc, page, pageSize, ct);

    [HttpGet("organizers/{organizerId:guid}/audit")]
    [AllowAnonymous]
    public async Task<IActionResult> OrganizerAudit(Guid organizerId, [FromQuery] string? actorId, [FromQuery] string? action,
        [FromQuery] string? entityType, [FromQuery] string? search, [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        if (User.IsInRole(AppRoles.SystemAdministrator)) return await QueryAudit(organizerId, actorId, action, entityType, search, fromUtc, toUtc, page, pageSize, ct);
        var userId = users.GetUserId(User);
        var authorized = await db.OrganizerMemberships.AnyAsync(x => x.OrganizerId == organizerId && x.UserId == userId && x.IsActive && x.Role == OrganizerRole.EventAdministrator && x.Organizer.IsActive && !x.Organizer.IsSuspended, ct);
        if (!authorized) return Forbid();
        return await QueryAudit(organizerId, actorId, action, entityType, search, fromUtc, toUtc, page, pageSize, ct);
    }

    private async Task<IActionResult> QueryAudit(Guid? organizerId, string? actorId, string? action, string? entityType, string? search,
        DateTime? fromUtc, DateTime? toUtc, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 100 || (fromUtc.HasValue && toUtc.HasValue && fromUtc > toUtc))
            return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid page, pageSize or UTC date range." });
        var query = db.AuditRecords.AsNoTracking().AsQueryable();
        if (organizerId.HasValue) query = query.Where(x => x.OrganizerId == organizerId);
        if (!string.IsNullOrWhiteSpace(actorId)) query = query.Where(x => x.ActorUserId == actorId);
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(x => x.Action.Contains(action.Trim()));
        if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(x => x.TargetEntityType == entityType.Trim());
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim(); query = query.Where(x => x.ActorIdentity!.Contains(term) || x.TargetEntityId.Contains(term) || x.Action.Contains(term)); }
        if (fromUtc.HasValue) query = query.Where(x => x.TimestampUtc >= fromUtc.Value);
        if (toUtc.HasValue) query = query.Where(x => x.TimestampUtc <= toUtc.Value);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.TimestampUtc).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.ActorUserId, x.ActorIdentity, x.Action, x.TargetEntityType, x.TargetEntityId, x.OrganizerId, x.TimestampUtc, x.MetadataJson }).ToListAsync(ct);
        return Ok(new Page<object>(rows.Select(x => (object)x).ToList(), page, pageSize, total));
    }

    private static string StatusOf(bool active, bool suspended) => !active ? "Inactive" : suspended ? "Suspended" : "Active";
    private enum OrganizerPlatformStatus { Active, Suspended, Inactive }
}
