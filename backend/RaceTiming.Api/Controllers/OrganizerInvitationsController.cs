using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Api.Services;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Identity;
using RaceTiming.Infrastructure.Persistence;

namespace RaceTiming.Api.Controllers;

[ApiController, Authorize, Route("api/v1")]
public sealed class OrganizerInvitationsController(
    RaceTimingDbContext db, UserManager<ApplicationUser> users, IInvitationEmailSender emailSender,
    IConfiguration configuration, IHostEnvironment environment) : ControllerBase
{
    public sealed record CreateInvitationRequest([Required, EmailAddress, StringLength(254)] string Email, OrganizerRole Role);
    public sealed record TokenRequest([Required] string Token);
    public sealed record RegisterRequest([Required] string Token, [Required, StringLength(100)] string DisplayName,
        [Required, EmailAddress, StringLength(254)] string Email, [Required] string Password);
    public sealed record InvitationDto(Guid Id, string Email, OrganizerRole Role, string Status, DateTime ExpiresAtUtc, DateTime CreatedAtUtc);

    [HttpGet("organizers/{organizerId:guid}/invitations")]
    public async Task<IActionResult> List(Guid organizerId, CancellationToken ct)
    {
        if (!await CanAdminister(organizerId, ct)) return Forbid();
        var now = DateTime.UtcNow;
        var items = await db.OrganizerInvitations.AsNoTracking().Where(x => x.OrganizerId == organizerId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new { x.Id, x.Email, x.Role, x.Status, x.ExpiresAtUtc, x.CreatedAtUtc })
            .ToListAsync(ct);
        return Ok(items.Select(x => new InvitationDto(x.Id, x.Email, x.Role,
            x.Status == OrganizerInvitationStatus.Pending && x.ExpiresAtUtc <= now ? "Expired" : x.Status.ToString(), x.ExpiresAtUtc, x.CreatedAtUtc)));
    }

    [HttpPost("organizers/{organizerId:guid}/invitations")]
    public async Task<IActionResult> Create(Guid organizerId, CreateInvitationRequest request, CancellationToken ct)
    {
        if (!await CanAdminister(organizerId, ct)) return Forbid();
        if (!IsAssignableRole(request.Role)) return BadRequest(new ProblemDetails { Status = 400, Title = "Only EventAdministrator and RaceDirector roles can be assigned." });
        var email = request.Email.Trim();
        var normalizedEmail = users.NormalizeEmail(email)!;
        var expired = await db.OrganizerInvitations.Where(x => x.OrganizerId == organizerId && x.Status == OrganizerInvitationStatus.Pending && x.ExpiresAtUtc <= DateTime.UtcNow).ToListAsync(ct);
        foreach (var item in expired) { item.Status = OrganizerInvitationStatus.Expired; item.ActiveInvitationKey = null; }
        if (expired.Count > 0) await db.SaveChangesAsync(ct);
        var actorId = users.GetUserId(User)!;
        var existingUser = await users.FindByEmailAsync(email);
        if (existingUser is { IsActive: false })
            return Conflict(new ProblemDetails { Status = 409, Title = "This account cannot accept organizer invitations." });
        if (existingUser is { IsActive: true } && existingUser.Id == actorId)
            return BadRequest(new ProblemDetails { Status = 400, Title = "You cannot invite your own account to an elevated organizer role." });
        if (existingUser is { IsActive: true } && await db.OrganizerMemberships.AnyAsync(x => x.OrganizerId == organizerId && x.UserId == existingUser.Id && x.IsActive, ct))
            return Conflict(new ProblemDetails { Status = 409, Title = "This user is already an active member." });
        if (await db.OrganizerInvitations.AnyAsync(x => x.OrganizerId == organizerId && x.NormalizedEmail == normalizedEmail &&
                x.Status == OrganizerInvitationStatus.Pending && x.ExpiresAtUtc > DateTime.UtcNow, ct))
            return Conflict(new ProblemDetails { Status = 409, Title = "A pending invitation already exists for this email." });

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expiresAt = DateTime.UtcNow.AddDays(Math.Clamp(configuration.GetValue("Invitations:ExpirationDays", 7), 1, 30));
        var invitation = new OrganizerInvitation
        {
            OrganizerId = organizerId, Email = email, NormalizedEmail = normalizedEmail, Role = request.Role,
            TokenHash = HashToken(token), ExpiresAtUtc = expiresAt, CreatedByUserId = actorId,
            ActiveInvitationKey = $"{organizerId:N}:{normalizedEmail}"
        };
        db.OrganizerInvitations.Add(invitation);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return Conflict(new ProblemDetails { Status = 409, Title = "A pending invitation already exists for this email." }); }
        var url = BuildAcceptanceUrl(token);
        try
        {
            var organizerName = await db.Organizers.Where(x => x.Id == organizerId).Select(x => x.Name).SingleAsync(ct);
            await emailSender.SendAsync(email, organizerName, url, expiresAt, ct);
        }
        catch (Exception)
        {
            // Keep the pending record so an administrator can retry delivery; never log token or URL.
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails { Status = 502, Title = "Invitation was saved, but email delivery failed. Retry delivery from the invitations list." });
        }
        var result = new InvitationDto(invitation.Id, invitation.Email, invitation.Role, "Pending", invitation.ExpiresAtUtc, invitation.CreatedAtUtc);
        return StatusCode(StatusCodes.Status202Accepted, new
        {
            invitation = result,
            developmentAcceptanceUrl = environment.IsDevelopment() && string.IsNullOrWhiteSpace(configuration["Email:Smtp:Host"]) ? url : null
        });
    }

    [HttpPost("organizers/{organizerId:guid}/invitations/{invitationId:guid}/resend")]
    public async Task<IActionResult> Resend(Guid organizerId, Guid invitationId, CancellationToken ct)
    {
        if (!await CanAdminister(organizerId, ct)) return Forbid();
        var invitation = await db.OrganizerInvitations.Include(x => x.Organizer)
            .SingleOrDefaultAsync(x => x.Id == invitationId && x.OrganizerId == organizerId, ct);
        if (invitation is null) return NotFound();
        if (invitation.Status != OrganizerInvitationStatus.Pending) return Conflict(new ProblemDetails { Status = 409, Title = "Only pending invitations can be resent." });
        if (invitation.ExpiresAtUtc <= DateTime.UtcNow)
        {
            invitation.Status = OrganizerInvitationStatus.Expired;
            invitation.ActiveInvitationKey = null;
            await db.SaveChangesAsync(ct);
            return Conflict(new ProblemDetails { Status = 409, Title = "This invitation has expired. Create a new invitation." });
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        invitation.TokenHash = HashToken(token);
        invitation.ExpiresAtUtc = DateTime.UtcNow.AddDays(Math.Clamp(configuration.GetValue("Invitations:ExpirationDays", 7), 1, 30));
        await db.SaveChangesAsync(ct);
        var url = BuildAcceptanceUrl(token);
        try { await emailSender.SendAsync(invitation.Email, invitation.Organizer.Name, url, invitation.ExpiresAtUtc, ct); }
        catch (Exception) { return StatusCode(502, new ProblemDetails { Status = 502, Title = "Invitation was updated, but email delivery failed." }); }
        return Ok(new { developmentAcceptanceUrl = environment.IsDevelopment() && string.IsNullOrWhiteSpace(configuration["Email:Smtp:Host"]) ? url : null });
    }

    [HttpPost("organizers/{organizerId:guid}/invitations/{invitationId:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid organizerId, Guid invitationId, CancellationToken ct)
    {
        if (!await CanAdminister(organizerId, ct)) return Forbid();
        var invitation = await db.OrganizerInvitations.SingleOrDefaultAsync(x => x.Id == invitationId && x.OrganizerId == organizerId, ct);
        if (invitation is null) return NotFound();
        if (invitation.Status != OrganizerInvitationStatus.Pending) return Conflict(new ProblemDetails { Status = 409, Title = "Only pending invitations can be revoked." });
        if (invitation.ExpiresAtUtc <= DateTime.UtcNow)
        {
            invitation.Status = OrganizerInvitationStatus.Expired;
            invitation.ActiveInvitationKey = null;
            await db.SaveChangesAsync(ct);
            return Conflict(new ProblemDetails { Status = 409, Title = "This invitation has expired." });
        }
        invitation.Status = OrganizerInvitationStatus.Revoked;
        invitation.ActiveInvitationKey = null;
        invitation.RevokedAtUtc = DateTime.UtcNow;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(new ProblemDetails { Status = 409, Title = "This invitation has already been processed." }); }
        return NoContent();
    }

    [HttpPost("invitations/accept"), AllowAnonymous, EnableRateLimiting("invitation-public")]
    public async Task<IActionResult> Accept(TokenRequest request, CancellationToken ct)
    {
        if (!User.Identity?.IsAuthenticated ?? true) return Unauthorized(new ProblemDetails { Status = 401, Title = "Sign in with the invited account to accept this invitation." });
        var user = await users.GetUserAsync(User);
        if (user is null || !user.IsActive) return Unauthorized(new ProblemDetails { Status = 401, Title = "An active RaceBib account is required." });
        var invitation = await FindPending(request.Token, ct);
        if (invitation is null) return BadRequest(InvalidInvitation());
        if (!string.Equals(users.NormalizeEmail(user.Email), invitation.NormalizedEmail, StringComparison.Ordinal))
            return BadRequest(new ProblemDetails { Status = 400, Title = "Sign in with the email address that received this invitation." });
        var accepted = await AcceptForUser(invitation, user, ct);
        return accepted ? NoContent() : BadRequest(InvalidInvitation());
    }

    [HttpPost("invitations/reject"), Authorize, EnableRateLimiting("invitation-public")]
    public async Task<IActionResult> Reject(TokenRequest request, CancellationToken ct)
    {
        var user = await users.GetUserAsync(User);
        var invitation = await FindPending(request.Token, ct);
        if (user is null || invitation is null || !string.Equals(users.NormalizeEmail(user.Email), invitation.NormalizedEmail, StringComparison.Ordinal))
            return BadRequest(InvalidInvitation());
        invitation.Status = OrganizerInvitationStatus.Rejected;
        invitation.ActiveInvitationKey = null;
        try { await db.SaveChangesAsync(ct); return NoContent(); }
        catch (DbUpdateConcurrencyException) { return BadRequest(InvalidInvitation()); }
    }

    [HttpPost("invitations/register"), AllowAnonymous, EnableRateLimiting("invitation-public")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var invitation = await FindPending(request.Token, ct);
        if (invitation is null || !string.Equals(users.NormalizeEmail(request.Email.Trim()), invitation.NormalizedEmail, StringComparison.Ordinal))
            return BadRequest(InvalidInvitation());
        if (await users.FindByEmailAsync(request.Email.Trim()) is not null)
            return BadRequest(new ProblemDetails { Status = 400, Title = "This invitation cannot be registered. If you have an account, sign in to accept it." });
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = new ApplicationUser { UserName = request.Email.Trim(), Email = request.Email.Trim(), EmailConfirmed = true, DisplayName = request.DisplayName.Trim(), IsActive = true };
        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            await transaction.RollbackAsync(ct);
            return BadRequest(new { title = "Account could not be created.", errors = created.Errors.Select(x => x.Description) });
        }
        var accepted = await CompleteAcceptance(invitation, user, ct);
        if (!accepted) { await transaction.RollbackAsync(ct); return BadRequest(InvalidInvitation()); }
        await transaction.CommitAsync(ct);
        return StatusCode(StatusCodes.Status201Created, new { message = "Account created and invitation accepted. Sign in to continue." });
    }

    private async Task<bool> CanAdminister(Guid organizerId, CancellationToken ct) =>
        User.IsInRole("SystemAdministrator") || await db.OrganizerMemberships.AsNoTracking().AnyAsync(x => x.OrganizerId == organizerId && x.UserId == users.GetUserId(User) && x.IsActive && x.Role == OrganizerRole.EventAdministrator && x.Organizer.IsActive, ct);

    private async Task<OrganizerInvitation?> FindPending(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64) return null;
        var hash = HashToken(token);
        return await db.OrganizerInvitations.SingleOrDefaultAsync(x => x.TokenHash == hash && x.Status == OrganizerInvitationStatus.Pending && x.ExpiresAtUtc > DateTime.UtcNow, ct);
    }

    private async Task<bool> AcceptForUser(OrganizerInvitation invitation, ApplicationUser user, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await CompleteAcceptance(invitation, user, ct)) { await transaction.RollbackAsync(ct); return false; }
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task<bool> CompleteAcceptance(OrganizerInvitation invitation, ApplicationUser user, CancellationToken ct)
    {
        invitation.Status = OrganizerInvitationStatus.Accepted;
        invitation.ActiveInvitationKey = null;
        invitation.AcceptedAtUtc = DateTime.UtcNow;
        var membership = await db.OrganizerMemberships.SingleOrDefaultAsync(x => x.OrganizerId == invitation.OrganizerId && x.UserId == user.Id, ct);
        if (membership is null) db.OrganizerMemberships.Add(new OrganizerMembership { OrganizerId = invitation.OrganizerId, UserId = user.Id, Role = invitation.Role });
        else { membership.Role = invitation.Role; membership.IsActive = true; membership.JoinedAt = DateTime.UtcNow; }
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }

    private string BuildAcceptanceUrl(string token)
    {
        var configuredBaseUrl = configuration["Application:BaseUrl"];
        if (environment.IsDevelopment() && string.IsNullOrWhiteSpace(configuredBaseUrl)) configuredBaseUrl = "http://localhost:8080";
        if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var baseUri) ||
            (!environment.IsDevelopment() && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Application:BaseUrl must be an absolute HTTPS URL outside Development.");
        var baseUrl = configuredBaseUrl!.TrimEnd('/');
        return $"{baseUrl}/accept-invitation?token={Uri.EscapeDataString(token)}";
    }
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static bool IsAssignableRole(OrganizerRole role) => role is OrganizerRole.EventAdministrator or OrganizerRole.RaceDirector;
    private static ProblemDetails InvalidInvitation() => new() { Status = 400, Title = "This invitation is invalid, expired, revoked, or already used." };
}
