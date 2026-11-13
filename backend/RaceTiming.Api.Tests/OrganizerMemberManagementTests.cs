using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RaceTiming.Api.Controllers;
using RaceTiming.Api.Services;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Identity;
using RaceTiming.Infrastructure.Persistence;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed class OrganizerMemberManagementTests : IAsyncLifetime
{
    private readonly string databaseName = $"member-tests-{Guid.NewGuid():N}";
    private ServiceProvider services = null!;
    private RaceTimingDbContext db = null!;
    private UserManager<ApplicationUser> users = null!;
    private Organizer organizerA = null!, organizerB = null!;
    private ApplicationUser admin = null!, director = null!, target = null!;
    private readonly TestEmailSender sender = new();

    public async Task InitializeAsync()
    {
        var collection = new ServiceCollection();
        collection.AddLogging(); collection.AddHttpContextAccessor();
        collection.AddDbContext<RaceTimingDbContext>(o => o.UseInMemoryDatabase(databaseName).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        collection.AddIdentityCore<ApplicationUser>(o => { o.User.RequireUniqueEmail = true; o.Password.RequiredLength = 12; o.Lockout.AllowedForNewUsers = true; })
            .AddRoles<IdentityRole>().AddEntityFrameworkStores<RaceTimingDbContext>().AddSignInManager();
        services = collection.BuildServiceProvider(); db = services.GetRequiredService<RaceTimingDbContext>(); await db.Database.EnsureCreatedAsync(); users = services.GetRequiredService<UserManager<ApplicationUser>>();
        admin = await NewUser("admin@example.test"); director = await NewUser("director@example.test"); target = await NewUser("target@example.test");
        organizerA = new Organizer { Name = "North Trail", Slug = "north-trail" }; organizerB = new Organizer { Name = "Coastal Running", Slug = "coastal-running" };
        db.Organizers.AddRange(organizerA, organizerB);
        db.OrganizerMemberships.Add(new OrganizerMembership { OrganizerId = organizerA.Id, UserId = admin.Id, Role = OrganizerRole.EventAdministrator });
        db.OrganizerMemberships.Add(new OrganizerMembership { OrganizerId = organizerA.Id, UserId = director.Id, Role = OrganizerRole.RaceDirector });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() { await db.Database.EnsureDeletedAsync(); await services.DisposeAsync(); }

    [Fact]
    public async Task EventAdministratorCanListMembersAndSearchUsers()
    {
        var list = await MembersController(admin).List(organizerA.Id, null, 1, 20, default);
        Assert.Equal(2, Assert.IsType<OkObjectResult>(list).Value!.GetType().GetProperty("total")!.GetValue(Assert.IsType<OkObjectResult>(list).Value));
        var search = await MembersController(admin).SearchUsers(organizerA.Id, "target", default);
        Assert.Equal(target.Id, Assert.Single(Assert.IsType<OkObjectResult>(search).Value as IEnumerable<OrganizerMembersController.UserDto> ?? []).Id);
    }

    [Fact]
    public async Task AddExistingMemberPreventsDuplicatesAndOnlyAllowsOrganizerRoles()
    {
        var c = MembersController(admin);
        var added = await c.Add(organizerA.Id, new OrganizerMembersController.AddMemberRequest(target.Email!, OrganizerRole.RaceDirector), default);
        Assert.IsType<ObjectResult>(added);
        Assert.Equal(OrganizerRole.RaceDirector, (await db.OrganizerMemberships.SingleAsync(x => x.UserId == target.Id)).Role);
        Assert.Contains(await db.AuditRecords.Select(x => x.Action).ToListAsync(), x => x == "Organizer.MemberAdded");
        Assert.IsType<ConflictObjectResult>(await c.Add(organizerA.Id, new OrganizerMembersController.AddMemberRequest(target.Email!, OrganizerRole.RaceDirector), default));
        Assert.IsType<BadRequestObjectResult>(await c.Add(organizerA.Id, new OrganizerMembersController.AddMemberRequest("other@example.test", OrganizerRole.TimingOfficial), default));
    }

    [Fact]
    public async Task RaceDirectorCannotAdministerMembersAndCrossOrganizerIsDenied()
    {
        db.OrganizerMemberships.Add(new OrganizerMembership { OrganizerId = organizerA.Id, UserId = target.Id, Role = OrganizerRole.RaceDirector });
        await db.SaveChangesAsync();
        var c = MembersController(director);
        Assert.IsType<ForbidResult>(await c.List(organizerA.Id, null, 1, 20, default));
        Assert.IsType<ForbidResult>(await c.List(organizerB.Id, null, 1, 20, default));
        var targetMembership = await db.OrganizerMemberships.SingleAsync(x => x.UserId == target.Id);
        Assert.IsType<ForbidResult>(await c.ChangeRole(organizerA.Id, targetMembership.Id,
            new OrganizerMembersController.UpdateRoleRequest(OrganizerRole.EventAdministrator), default));
    }

    [Fact]
    public async Task DeactivationImmediatelyRemovesMembershipAndReactivationRestoresIt()
    {
        var membership = await db.OrganizerMemberships.SingleAsync(x => x.UserId == director.Id);
        var c = MembersController(admin);
        Assert.IsType<NoContentResult>(await c.SetStatus(organizerA.Id, membership.Id, false, default));
        Assert.False(await db.OrganizerMemberships.AnyAsync(x => x.UserId == director.Id && x.IsActive));
        Assert.False(await new RaceTiming.Api.Authorization.OrganizerMembershipAuthorization(db)
            .CanAccessAsync(Context(director).User, organizerA.Id, true, default));
        Assert.IsType<NoContentResult>(await c.SetStatus(organizerA.Id, membership.Id, true, default));
        Assert.True(await db.OrganizerMemberships.AnyAsync(x => x.UserId == director.Id && x.IsActive));
        Assert.True(await new RaceTiming.Api.Authorization.OrganizerMembershipAuthorization(db)
            .CanAccessAsync(Context(director).User, organizerA.Id, true, default));
        var membershipActions = await db.AuditRecords.Select(x => x.Action).ToListAsync();
        Assert.Contains("Organizer.MembershipDeactivated", membershipActions);
        Assert.Contains("Organizer.MembershipReactivated", membershipActions);
    }

    [Fact]
    public async Task UsersCannotAssignElevatedRolesToThemselves()
    {
        var c = MembersController(admin);
        Assert.IsType<BadRequestObjectResult>(await c.Add(organizerA.Id, new OrganizerMembersController.AddMemberRequest(admin.Email!, OrganizerRole.EventAdministrator), default));
        var own = await db.OrganizerMemberships.SingleAsync(x => x.UserId == admin.Id);
        Assert.IsType<BadRequestObjectResult>(await c.ChangeRole(organizerA.Id, own.Id, new OrganizerMembersController.UpdateRoleRequest(OrganizerRole.RaceDirector), default));
    }

    [Fact]
    public async Task EventAdministratorCanChangeRolesButCannotTouchAnotherOrganizersMembership()
    {
        var member = new OrganizerMembership { OrganizerId = organizerA.Id, UserId = target.Id, Role = OrganizerRole.RaceDirector };
        var other = new OrganizerMembership { OrganizerId = organizerB.Id, UserId = director.Id, Role = OrganizerRole.RaceDirector };
        db.OrganizerMemberships.AddRange(member, other); await db.SaveChangesAsync();
        var c = MembersController(admin);
        Assert.IsType<NoContentResult>(await c.ChangeRole(organizerA.Id, member.Id, new OrganizerMembersController.UpdateRoleRequest(OrganizerRole.EventAdministrator), default));
        Assert.Contains(await db.AuditRecords.Select(x => x.Action).ToListAsync(), x => x == "Organizer.MembershipRoleChanged");
        Assert.IsType<NotFoundResult>(await c.ChangeRole(organizerA.Id, other.Id, new OrganizerMembersController.UpdateRoleRequest(OrganizerRole.EventAdministrator), default));
        Assert.Equal(OrganizerRole.RaceDirector, (await db.OrganizerMemberships.SingleAsync(x => x.Id == other.Id)).Role);
    }

    [Fact]
    public async Task InvitationStoresOnlyHashAndRejectsDuplicatePendingEmailCaseInsensitively()
    {
        var c = InvitationController(admin);
        Assert.Equal(202, Assert.IsType<ObjectResult>(await c.Create(organizerA.Id, new OrganizerInvitationsController.CreateInvitationRequest("new@example.test", OrganizerRole.RaceDirector), default)).StatusCode);
        var invitation = await db.OrganizerInvitations.SingleAsync();
        var token = ExtractToken(sender.LastUrl!);
        Assert.NotEqual(token, invitation.TokenHash);
        Assert.Equal(64, invitation.TokenHash.Length);
        Assert.Contains(await db.AuditRecords.Select(x => x.Action).ToListAsync(), x => x == "Organizer.InvitationCreated");
        Assert.IsType<ConflictObjectResult>(await c.Create(organizerA.Id, new OrganizerInvitationsController.CreateInvitationRequest("NEW@example.test", OrganizerRole.EventAdministrator), default));
    }

    [Fact]
    public async Task InactiveIdentityAccountCannotReceiveAnUnusableInvitation()
    {
        var inactive = await NewUser("inactive@example.test");
        inactive.IsActive = false;
        await db.SaveChangesAsync();
        var result = await InvitationController(admin).Create(organizerA.Id,
            new OrganizerInvitationsController.CreateInvitationRequest(inactive.Email!, OrganizerRole.RaceDirector), default);
        Assert.IsType<ConflictObjectResult>(result);
        Assert.Empty(await db.OrganizerInvitations.ToListAsync());
    }

    [Fact]
    public async Task RaceDirectorCannotInviteAndSystemAdministratorCanAdministerOtherOrganizers()
    {
        Assert.IsType<ForbidResult>(await InvitationController(director).Create(organizerA.Id, new OrganizerInvitationsController.CreateInvitationRequest("new@example.test", OrganizerRole.RaceDirector), default));
        var systemAdmin = await NewUser("system@example.test");
        var result = await InvitationController(systemAdmin, "SystemAdministrator").Create(organizerB.Id,
            new OrganizerInvitationsController.CreateInvitationRequest("new@example.test", OrganizerRole.EventAdministrator), default);
        Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task ExistingUserCanAcceptOnlyMatchingInvitationOnce()
    {
        var c = InvitationController(admin);
        await c.Create(organizerA.Id, new OrganizerInvitationsController.CreateInvitationRequest(target.Email!, OrganizerRole.RaceDirector), default);
        var token = ExtractToken(sender.LastUrl!);
        var accept = InvitationController(target);
        Assert.IsType<NoContentResult>(await accept.Accept(new OrganizerInvitationsController.TokenRequest(token), default));
        Assert.True(await db.OrganizerMemberships.AnyAsync(x => x.UserId == target.Id && x.OrganizerId == organizerA.Id && x.IsActive && x.Role == OrganizerRole.RaceDirector));
        Assert.Contains(await db.AuditRecords.Select(x => x.Action).ToListAsync(), x => x == "Organizer.InvitationAccepted");
        Assert.IsType<BadRequestObjectResult>(await accept.Accept(new OrganizerInvitationsController.TokenRequest(token), default));
        Assert.Empty(await users.GetRolesAsync(target));
    }

    [Fact]
    public async Task ResendingRotatesTokenAndRejectingConsumesInvitation()
    {
        var c = InvitationController(admin);
        await c.Create(organizerA.Id, new OrganizerInvitationsController.CreateInvitationRequest(target.Email!, OrganizerRole.RaceDirector), default);
        var originalToken = ExtractToken(sender.LastUrl!);
        var invitation = await db.OrganizerInvitations.SingleAsync();
        Assert.IsType<OkObjectResult>(await c.Resend(organizerA.Id, invitation.Id, default));
        var resentToken = ExtractToken(sender.LastUrl!);
        Assert.NotEqual(originalToken, resentToken);
        Assert.Contains(await db.AuditRecords.Select(x => x.Action).ToListAsync(), x => x == "Organizer.InvitationResent");
        Assert.IsType<BadRequestObjectResult>(await InvitationController(target).Accept(new OrganizerInvitationsController.TokenRequest(originalToken), default));
        Assert.IsType<NoContentResult>(await InvitationController(target).Reject(new OrganizerInvitationsController.TokenRequest(resentToken), default));
        Assert.Equal(OrganizerInvitationStatus.Rejected, (await db.OrganizerInvitations.SingleAsync()).Status);
        Assert.Contains(await db.AuditRecords.Select(x => x.Action).ToListAsync(), x => x == "Organizer.InvitationRejected");
        Assert.IsType<BadRequestObjectResult>(await InvitationController(target).Accept(new OrganizerInvitationsController.TokenRequest(resentToken), default));
    }

    [Fact]
    public async Task InvitationEmailMustMatchAuthenticatedUser()
    {
        await InvitationController(admin).Create(organizerA.Id, new OrganizerInvitationsController.CreateInvitationRequest("target@example.test", OrganizerRole.RaceDirector), default);
        Assert.IsType<BadRequestObjectResult>(await InvitationController(director).Accept(new OrganizerInvitationsController.TokenRequest(ExtractToken(sender.LastUrl!)), default));
    }

    [Fact]
    public async Task InvitationCanProvisionNewAccountAndPasswordPolicyIsEnforced()
    {
        await InvitationController(admin).Create(organizerA.Id, new OrganizerInvitationsController.CreateInvitationRequest("newuser@example.test", OrganizerRole.EventAdministrator), default);
        var c = InvitationController(admin);
        var token = ExtractToken(sender.LastUrl!);
        var weak = await c.Register(new OrganizerInvitationsController.RegisterRequest(token, "New User", "newuser@example.test", "weak"), default);
        Assert.IsType<BadRequestObjectResult>(weak);
        var valid = await c.Register(new OrganizerInvitationsController.RegisterRequest(token, "New User", "newuser@example.test", "RaceBib!Valid-123"), default);
        Assert.Equal(201, Assert.IsType<ObjectResult>(valid).StatusCode);
        var created = await users.FindByEmailAsync("newuser@example.test");
        Assert.NotNull(created);
        Assert.True(created!.EmailConfirmed);
        Assert.True(await db.OrganizerMemberships.AnyAsync(x => x.UserId == created.Id && x.OrganizerId == organizerA.Id));
        Assert.Empty(await users.GetRolesAsync(created));
    }

    [Fact]
    public async Task ExpiredAndRevokedInvitationTokensAreRejected()
    {
        var c = InvitationController(admin);
        await c.Create(organizerA.Id, new OrganizerInvitationsController.CreateInvitationRequest("target@example.test", OrganizerRole.RaceDirector), default);
        var invitation = await db.OrganizerInvitations.SingleAsync();
        var token = ExtractToken(sender.LastUrl!);
        invitation.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await InvitationController(target).Accept(new OrganizerInvitationsController.TokenRequest(token), default));
        Assert.IsType<ConflictObjectResult>(await c.Resend(organizerA.Id, invitation.Id, default));
        Assert.Contains(await db.AuditRecords.Select(x => x.Action).ToListAsync(), x => x == "Organizer.InvitationExpired");
        invitation.ExpiresAtUtc = DateTime.UtcNow.AddDays(1); invitation.Status = OrganizerInvitationStatus.Pending; await db.SaveChangesAsync();
        Assert.IsType<NoContentResult>(await c.Revoke(organizerA.Id, invitation.Id, default));
        Assert.Contains(await db.AuditRecords.Select(x => x.Action).ToListAsync(), x => x == "Organizer.InvitationRevoked");
        Assert.IsType<BadRequestObjectResult>(await InvitationController(target).Accept(new OrganizerInvitationsController.TokenRequest(token), default));
    }

    [Fact]
    public async Task EmailFailureIsReportedWithoutReturningTokenAndInvitationCanBeListed()
    {
        sender.Fail = true;
        var result = await InvitationController(admin).Create(organizerA.Id, new OrganizerInvitationsController.CreateInvitationRequest("new@example.test", OrganizerRole.RaceDirector), default);
        Assert.Equal(502, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Single(await db.OrganizerInvitations.ToListAsync());
        Assert.Null((Assert.IsType<ObjectResult>(result).Value as ProblemDetails)?.Detail);
    }

    [Fact]
    public async Task InactiveMembershipDoesNotGrantAdministration()
    {
        var membership = await db.OrganizerMemberships.SingleAsync(x => x.UserId == admin.Id);
        membership.IsActive = false; await db.SaveChangesAsync();
        Assert.IsType<ForbidResult>(await MembersController(admin).List(organizerA.Id, null, 1, 20, default));
    }

    private async Task<ApplicationUser> NewUser(string email)
    {
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = email.Split('@')[0] };
        var result = await users.CreateAsync(user, "RaceBib!Test-Password123");
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        return user;
    }

    private OrganizerMembersController MembersController(ApplicationUser user, string? globalRole = null) =>
        new(db, users, new AuditLogger(db)) { ControllerContext = new ControllerContext { HttpContext = Context(user, globalRole) } };

    private OrganizerInvitationsController InvitationController(ApplicationUser user, string? globalRole = null) =>
        new(db, users, sender, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Application:BaseUrl"] = "http://localhost:8080", ["Invitations:ExpirationDays"] = "7"
        }).Build(), new TestEnvironment(), new AuditLogger(db)) { ControllerContext = new ControllerContext { HttpContext = Context(user, globalRole) } };

    private static DefaultHttpContext Context(ApplicationUser user, string? role = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Name, user.UserName!) };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return context;
    }

    private static string ExtractToken(string url) => Uri.UnescapeDataString(new Uri(url).Query.Split("token=")[1]);

    private sealed class TestEmailSender : IInvitationEmailSender
    {
        public bool Fail { get; set; }
        public string? LastUrl { get; private set; }
        public Task SendAsync(string email, string organizerName, string acceptanceUrl, DateTime expiresAtUtc, CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("Simulated email failure.");
            LastUrl = acceptanceUrl; return Task.CompletedTask;
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "RaceBib.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
