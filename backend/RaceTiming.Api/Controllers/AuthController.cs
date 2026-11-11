using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Identity;
using RaceTiming.Infrastructure.Persistence;

namespace RaceTiming.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signInManager,
    RaceTimingDbContext db,
    IConfiguration configuration) : ControllerBase
{
    public sealed record LoginRequest(string Email, string Password);
    public sealed record OrganizerMembershipInfo(Guid OrganizerId, string OrganizerName, OrganizerRole Role);
    public sealed record UserProfile(
        string Id, string? Email, string? DisplayName, IList<string> Roles,
        IReadOnlyList<OrganizerMembershipInfo> OrganizerMemberships);
    public sealed record LoginResponse(string AccessToken, DateTime ExpiresAtUtc, UserProfile User);
    
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email);
        if (user is null || !user.IsActive)
            return Unauthorized(new ProblemDetails { Status = 401, Title = "Invalid email or password." });

        // CheckPasswordSignInAsync applies Identity lockout rules without issuing an
        // authentication cookie. JWT remains the API's only authentication scheme.
        var result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (result.IsLockedOut || await users.IsLockedOutAsync(user))
            return StatusCode(StatusCodes.Status423Locked, new ProblemDetails
            {
                Status = StatusCodes.Status423Locked,
                Title = "This account is temporarily locked after too many failed sign-in attempts."
            });
        if (!result.Succeeded)
            return Unauthorized(new ProblemDetails { Status = 401, Title = "Invalid email or password." });

        var key = configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
            return StatusCode(500, new ProblemDetails { Status = 500, Title = "JWT signing key is not configured securely." });

        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(configuration.GetValue<int>("Jwt:AccessTokenMinutes", 15));
        var profile = await BuildUserProfile(user, ct);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.DisplayName ?? user.Email ?? user.UserName ?? user.Id)
        };
        claims.AddRange(profile.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"],
            audience: configuration["Jwt:Audience"],
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                SecurityAlgorithms.HmacSha256));

        return Ok(new LoginResponse(
            new JwtSecurityTokenHandler().WriteToken(token), expires,
            profile));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var user = await users.GetUserAsync(User);
        if (user is null || !user.IsActive) return Unauthorized();
        return Ok(await BuildUserProfile(user, ct));
    }

    private async Task<UserProfile> BuildUserProfile(ApplicationUser user, CancellationToken ct)
    {
        var memberships = await db.OrganizerMemberships.AsNoTracking()
            .Where(x => x.UserId == user.Id && x.IsActive && x.Organizer.IsActive)
            .OrderBy(x => x.Organizer.Name)
            .Select(x => new OrganizerMembershipInfo(x.OrganizerId, x.Organizer.Name, x.Role))
            .ToListAsync(ct);

        return new UserProfile(user.Id, user.Email, user.DisplayName,
            await users.GetRolesAsync(user), memberships);
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var jwtId = User.FindFirstValue(JwtRegisteredClaimNames.Jti);
        var expiryClaim = User.FindFirstValue(JwtRegisteredClaimNames.Exp);
        if (string.IsNullOrWhiteSpace(jwtId) || !long.TryParse(expiryClaim, out var expiry))
            return BadRequest(new ProblemDetails { Status = 400, Title = "Token cannot be revoked." });

        var alreadyRevoked = await db.RevokedTokens.AnyAsync(x => x.JwtId == jwtId, ct);
        if (!alreadyRevoked)
        {
            db.RevokedTokens.Add(new RevokedToken
            {
                JwtId = jwtId,
                ExpiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expiry).UtcDateTime
            });
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }
}
