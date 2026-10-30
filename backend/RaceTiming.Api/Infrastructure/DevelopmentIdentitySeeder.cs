using Microsoft.AspNetCore.Identity;
using RaceTiming.Api.Authorization;
using RaceTiming.Infrastructure.Identity;

namespace RaceTiming.Api.Infrastructure;

public static class DevelopmentIdentitySeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration)
    {
        using var scope = services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        foreach (var role in AppRoles.All)
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));

        var email = configuration["SeedAdmin:Email"];
        var password = configuration["SeedAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return;

        var admin = await userManager.FindByEmailAsync(email);
        if (admin is null)
        {
            admin = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = "System Administrator",
                IsActive = true
            };
            var created = await userManager.CreateAsync(admin, password);
            if (!created.Succeeded)
                throw new InvalidOperationException("Administrator seed failed: " +
                    string.Join("; ", created.Errors.Select(x => x.Description)));
        }

        if (!await userManager.IsInRoleAsync(admin, AppRoles.SystemAdministrator))
            await userManager.AddToRoleAsync(admin, AppRoles.SystemAdministrator);
    }
}
