using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RaceTiming.Api.Authorization;
using RaceTiming.Api.Controllers;
using RaceTiming.Api.Services;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Identity;
using RaceTiming.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed class PlatformAdministrationTests : IAsyncLifetime
{
    private readonly string database = $"platform-tests-{Guid.NewGuid():N}";
    private ServiceProvider services = null!;
    private RaceTimingDbContext db = null!;
    private UserManager<ApplicationUser> users = null!;
    private RoleManager<IdentityRole> roles = null!;
    private ApplicationUser admin = null!, secondAdmin = null!, ordinary = null!;
    private Organizer organizerA = null!, organizerB = null!;

    public async Task InitializeAsync()
    {
        var collection = new ServiceCollection(); collection.AddLogging();
        collection.AddDbContext<RaceTimingDbContext>(o => o.UseInMemoryDatabase(database).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        collection.AddIdentityCore<ApplicationUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<RaceTimingDbContext>().AddSignInManager();
        services = collection.BuildServiceProvider(); db = services.GetRequiredService<RaceTimingDbContext>(); await db.Database.EnsureCreatedAsync();
        users = services.GetRequiredService<UserManager<ApplicationUser>>(); roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        await roles.CreateAsync(new IdentityRole(AppRoles.SystemAdministrator));
        admin = await NewUser("platform-admin@example.test"); secondAdmin = await NewUser("platform-admin-two@example.test"); ordinary = await NewUser("member@example.test");
        await users.AddToRoleAsync(admin, AppRoles.SystemAdministrator); await users.AddToRoleAsync(secondAdmin, AppRoles.SystemAdministrator);
        organizerA = new Organizer { Name = "North", Slug = "north" }; organizerB = new Organizer { Name = "South", Slug = "south" };
        db.Organizers.AddRange(organizerA, organizerB);
        db.OrganizerMemberships.Add(new OrganizerMembership { OrganizerId = organizerA.Id, UserId = ordinary.Id, Role = OrganizerRole.EventAdministrator });
        await db.SaveChangesAsync();
    }
    public async Task DisposeAsync() { await db.Database.EnsureDeletedAsync(); await services.DisposeAsync(); }

    [Fact]
    public Task PlatformAuthorizationRequiresSystemAdministratorRole()
    {
        var attr = typeof(PlatformAdministrationController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single();
        Assert.Equal(AppRoles.SystemAdministrator, attr.Roles);
        Assert.Empty(typeof(PlatformAdministrationController).GetMethod(nameof(PlatformAdministrationController.ListUsers))!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true));
        Assert.Single(typeof(PlatformAdministrationController).GetMethod(nameof(PlatformAdministrationController.OrganizerAudit))!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true));
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SystemAdministratorCanGrantAndRevokePlatformRole()
    {
        var controller = Controller(admin);
        Assert.IsType<NoContentResult>(await controller.SetPlatformAdmin(ordinary.Id, new(true), default));
        Assert.True(await users.IsInRoleAsync(ordinary, AppRoles.SystemAdministrator));
        Assert.IsType<NoContentResult>(await controller.SetPlatformAdmin(ordinary.Id, new(false), default));
        Assert.False(await users.IsInRoleAsync(ordinary, AppRoles.SystemAdministrator));
        Assert.Contains(await db.AuditRecords.Select(x => x.Action).ToListAsync(), x => x == "Platform.AdministratorAssigned");
    }

    [Fact]
    public async Task LastActivePlatformAdministratorCannotBeRemovedOrDeactivated()
    {
        await users.RemoveFromRoleAsync(secondAdmin, AppRoles.SystemAdministrator);
        var controller = Controller(admin);
        Assert.IsType<ConflictObjectResult>(await controller.SetPlatformAdmin(admin.Id, new(false), default));
        Assert.IsType<BadRequestObjectResult>(await controller.SetUserStatus(admin.Id, new(false), default)); // Self-deactivation is rejected before last-admin evaluation.
        Assert.True(await users.IsInRoleAsync(admin, AppRoles.SystemAdministrator));
        Assert.True((await users.FindByIdAsync(admin.Id))!.IsActive);
    }

    [Fact]
    public async Task SuspendingOrganizerBlocksMemberAndReactivationRestoresAccessWithoutDeletingEvents()
    {
        var eventItem = new Event { OrganizerId = organizerA.Id, Name = "Historic 10K", Slug = "historic-10k", OrganizerName = organizerA.Name,
            StartDate = DateTime.UtcNow.AddDays(10), EndDate = DateTime.UtcNow.AddDays(10).AddHours(3), TimeZoneId = "UTC" };
        db.Events.Add(eventItem); await db.SaveChangesAsync();
        var auth = new OrganizerMembershipAuthorization(db);
        var principal = Principal(ordinary);
        Assert.True(await auth.CanAccessAsync(principal, organizerA.Id, false, default));
        var c = Controller(admin);
        Assert.IsType<NoContentResult>(await c.SetOrganizerStatus(organizerA.Id, new("Suspended"), default));
        Assert.False(await auth.CanAccessAsync(principal, organizerA.Id, false, default));
        Assert.True(await db.Events.AnyAsync(x => x.Id == eventItem.Id));
        Assert.IsType<NoContentResult>(await c.SetOrganizerStatus(organizerA.Id, new("Active"), default));
        Assert.True(await auth.CanAccessAsync(principal, organizerA.Id, false, default));
        Assert.Contains(await db.AuditRecords.Select(x => x.Action).ToListAsync(), x => x == "Platform.OrganizerSuspended");
    }

    [Fact]
    public async Task UndefinedNumericOrganizerStatusIsRejectedWithoutChangingState()
    {
        var result = await Controller(admin).SetOrganizerStatus(organizerA.Id, new("99"), default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.True(organizerA.IsActive);
        Assert.False(organizerA.IsSuspended);
        Assert.Empty(await db.AuditRecords.ToListAsync());
    }

    [Fact]
    public async Task NonPlatformUserCannotReadOtherOrganizerAudit()
    {
        var c = Controller(ordinary);
        Assert.IsType<ForbidResult>(await c.OrganizerAudit(organizerB.Id, null, null, null, null, null, null, 1, 50, default));
    }

    [Fact]
    public async Task PlatformAdministratorCanReadAuditAndMetadataIsSanitized()
    {
        var audit = new AuditLogger(db);
        audit.Record(Principal(admin), "Platform.Test", "User", ordinary.Id,
            metadata: new Dictionary<string, object?>
            {
                ["role"] = "EventAdministrator", ["invitationToken"] = "invitation-value",
                ["password"] = "password-value", ["jwt"] = "jwt-value", ["refreshToken"] = "refresh-value",
                ["apiKey"] = "api-key-value", ["signingKey"] = "signing-key-value", ["authorization"] = "bearer-value",
                ["cookie"] = "cookie-value", ["credential"] = "credential-value", ["email"] = "email-value",
                ["note"] = "innocent-key-jwt-value"
            });
        await db.SaveChangesAsync();
        var stored = await db.AuditRecords.SingleAsync();
        Assert.Contains("EventAdministrator", stored.MetadataJson);
        Assert.DoesNotContain("value", stored.MetadataJson);
        audit.Record(Principal(admin), "Platform.TestUnsafeValue", "User", ordinary.Id,
            metadata: new Dictionary<string, object?> { ["role"] = "jwt-hidden-in-allowed-field" });
        await db.SaveChangesAsync();
        var allMetadata = string.Join("|", await db.AuditRecords.Select(x => x.MetadataJson).ToListAsync());
        Assert.DoesNotContain("jwt-hidden-in-allowed-field", allMetadata);
        var result = await Controller(admin).PlatformAudit(null, null, null, null, null, null, 1, 50, default);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public Task AuditControllerExposesNoMutationActions()
    {
        var methods = typeof(PlatformAdministrationController).GetMethods();
        Assert.DoesNotContain(methods, m => m.Name.Contains("DeleteAudit", StringComparison.Ordinal));
        Assert.DoesNotContain(methods, m => m.Name.Contains("UpdateAudit", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private async Task<ApplicationUser> NewUser(string email)
    {
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = email };
        var result = await users.CreateAsync(user, "RaceBib!Strong-Test-123");
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        return user;
    }
    private PlatformAdministrationController Controller(ApplicationUser user) => new(db, users, roles, new AuditLogger(db))
    { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = Principal(user) } } };
    private static ClaimsPrincipal Principal(ApplicationUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Email, user.Email!),
            new(ClaimTypes.Name, user.Email!)
        };
        if (user.UserName?.StartsWith("platform-admin", StringComparison.Ordinal) == true)
            claims.Add(new Claim(ClaimTypes.Role, AppRoles.SystemAdministrator));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
