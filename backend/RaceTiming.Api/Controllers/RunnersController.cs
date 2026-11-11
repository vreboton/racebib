using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Domain.Entities;
using RaceTiming.Api.Authorization;
using RaceTiming.Infrastructure.Persistence;

namespace RaceTiming.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/organizers/{organizerId:guid}/runners")]
public sealed class RunnersController(
    RaceTimingDbContext db,
    IOrganizerMembershipAuthorization organizerAuthorization) : ControllerBase
{
    public sealed record CreateRunnerRequest(
        string FirstName, string LastName, string? PublicDisplayName, string? Email);

    public sealed record UpdateRunnerRequest(
        string FirstName, string LastName, string? PublicDisplayName, string? Email);

    [HttpGet]
    public async Task<IActionResult> List(Guid organizerId, [FromQuery] string? search = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        if (!await organizerAuthorization.CanAccessAsync(User, organizerId, false, ct)) return Forbid();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Runners.AsNoTracking().Where(x => x.OrganizerId == organizerId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.FirstName.Contains(term) ||
                x.LastName.Contains(term) ||
                (x.Email != null && x.Email.Contains(term)) ||
                (x.PublicDisplayName != null && x.PublicDisplayName.Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.LastName).ThenBy(x => x.FirstName)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.FirstName, x.LastName, x.PublicDisplayName, x.Email, x.CreatedAt })
            .ToListAsync(ct);

        return Ok(new { items, page, pageSize, total, totalPages = (int)Math.Ceiling(total / (double)pageSize) });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid organizerId, Guid id, CancellationToken ct)
    {
        if (!await organizerAuthorization.CanAccessAsync(User, organizerId, false, ct)) return Forbid();
        var runner = await db.Runners.AsNoTracking()
            .Where(x => x.OrganizerId == organizerId && x.Id == id)
            .Select(x => new { x.Id, x.FirstName, x.LastName, x.PublicDisplayName, x.Email, x.CreatedAt })
            .FirstOrDefaultAsync(ct);
        return runner is null ? NotFound() : Ok(runner);
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid organizerId, CreateRunnerRequest request, CancellationToken ct)
    {
        if (!await organizerAuthorization.CanAccessAsync(User, organizerId, true, ct)) return Forbid();
        var validation = Validate(request.FirstName, request.LastName, request.Email);
        if (validation is not null) return BadRequest(validation);

        var runner = new Runner
        {
            OrganizerId = organizerId,
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            PublicDisplayName = NormalizeOptional(request.PublicDisplayName),
            Email = NormalizeEmail(request.Email)
        };
        db.Runners.Add(runner);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { organizerId, id = runner.Id }, ToDto(runner));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid organizerId, Guid id, UpdateRunnerRequest request, CancellationToken ct)
    {
        if (!await organizerAuthorization.CanAccessAsync(User, organizerId, true, ct)) return Forbid();
        var validation = Validate(request.FirstName, request.LastName, request.Email);
        if (validation is not null) return BadRequest(validation);

        var runner = await db.Runners.FirstOrDefaultAsync(
            x => x.OrganizerId == organizerId && x.Id == id, ct);
        if (runner is null) return NotFound();

        runner.FirstName = request.FirstName.Trim();
        runner.LastName = request.LastName.Trim();
        runner.PublicDisplayName = NormalizeOptional(request.PublicDisplayName);
        runner.Email = NormalizeEmail(request.Email);
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(runner));
    }

    private static ProblemDetails? Validate(string firstName, string lastName, string? email)
    {
        if (string.IsNullOrWhiteSpace(firstName) || firstName.Trim().Length > 100 ||
            string.IsNullOrWhiteSpace(lastName) || lastName.Trim().Length > 100)
            return new ProblemDetails { Status = 400, Title = "First and last name are required and must not exceed 100 characters." };
        if (!string.IsNullOrWhiteSpace(email) &&
            (email.Length > 254 || !Regex.IsMatch(email.Trim(), @"^[^\s@]+@[^\s@]+\.[^\s@]+$")))
            return new ProblemDetails { Status = 400, Title = "Email address is invalid." };
        return null;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeEmail(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static object ToDto(Runner runner) => new
    {
        runner.Id, runner.OrganizerId, runner.FirstName, runner.LastName,
        runner.PublicDisplayName, runner.Email, runner.CreatedAt
    };
}
