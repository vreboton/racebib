using Microsoft.AspNetCore.Identity;

namespace RaceTiming.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
    public bool IsActive { get; set; } = true;
}
