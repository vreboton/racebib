using Microsoft.AspNetCore.Identity;
using RaceTiming.Api.Authorization;
using RaceTiming.Infrastructure.Identity;
using RaceTiming.Infrastructure.Persistence;

namespace RaceTiming.Api.Infrastructure;

public static class DevelopmentIdentitySeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration, bool allowExistingAccountPromotion = true)
    {
        using var scope = services.CreateScope();
        // Production bootstrap is atomic; development keeps its existing provider-compatible behavior.
        await using var transaction = allowExistingAccountPromotion ? null :
            await scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>().Database.BeginTransactionAsync();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        foreach (var role in AppRoles.All)
            if (!await roleManager.RoleExistsAsync(role))
            {
                var result = await roleManager.CreateAsync(new IdentityRole(role));
                if (!result.Succeeded)
                    throw new InvalidOperationException("Role provisioning failed.");
            }

        var email = configuration["SeedAdmin:Email"];
        var password = configuration["SeedAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return;

        var admin = await userManager.FindByEmailAsync(email);
        if (admin is not null && !allowExistingAccountPromotion &&
            (!admin.IsActive || !await userManager.IsInRoleAsync(admin, AppRoles.SystemAdministrator) ||
             !await userManager.CheckPasswordAsync(admin, password)))
            throw new InvalidOperationException("Bootstrap will not promote, reactivate, or reset an existing account.");
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
        {
            var result = await userManager.AddToRoleAsync(admin, AppRoles.SystemAdministrator);
            if (!result.Succeeded)
                throw new InvalidOperationException("Administrator role assignment failed.");
        }
        if (transaction is not null) await transaction.CommitAsync();
    }
}
