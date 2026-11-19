using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RaceTiming.Api.Authorization;
using RaceTiming.Api.Controllers;
using RaceTiming.Api.Services;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Identity;
using RaceTiming.Infrastructure.Persistence;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed class AuthAndAuthorizationTests : IAsyncLifetime
{
    private readonly string password = $"RaceBib!Test-{Guid.NewGuid():N}";
    private readonly ServiceProvider services;
    private readonly string databaseName = $"racebib-tests-{Guid.NewGuid():N}";
    private RaceTimingDbContext db = null!;
    private UserManager<ApplicationUser> users = null!;
    private SignInManager<ApplicationUser> signInManager = null!;
    private ApplicationUser account = null!;
    private Organizer organizerA = null!;
    private Organizer organizerB = null!;

    public AuthAndAuthorizationTests()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddHttpContextAccessor();
        collection.AddAuthentication();
        collection.AddDbContext<RaceTimingDbContext>(options => options.UseInMemoryDatabase(databaseName));
        collection.AddScoped<IOrganizerMembershipAuthorization, OrganizerMembershipAuthorization>();
        collection.AddIdentityCore<ApplicationUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        })
        .AddRoles<IdentityRole>()
        .AddEntityFrameworkStores<RaceTimingDbContext>()
        .AddSignInManager();
        services = collection.BuildServiceProvider();
    }

    public async Task InitializeAsync()
    {
        db = services.GetRequiredService<RaceTimingDbContext>();
        await db.Database.EnsureCreatedAsync();
        users = services.GetRequiredService<UserManager<ApplicationUser>>();
        signInManager = services.GetRequiredService<SignInManager<ApplicationUser>>();

        var email = $"organizer-{Guid.NewGuid():N}@example.test";
        account = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "Organizer User"
        };
        var created = await users.CreateAsync(account, password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(x => x.Description)));

        organizerA = new Organizer { Name = "North Trail Club", Slug = "north-trail" };
        organizerB = new Organizer { Name = "Coastal Running Club", Slug = "coastal-running" };
        db.Organizers.AddRange(organizerA, organizerB);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await db.Database.EnsureDeletedAsync();
        await services.DisposeAsync();
    }

    [Fact]
    public async Task Login_WithValidCredentials_IssuesJwt()
    {
        await AddMembership(organizerA, OrganizerRole.RaceDirector);
        var result = await CreateAuthController().Login(new AuthController.LoginRequest(account.Email!, password), default);

        var response = Assert.IsType<AuthController.LoginResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.False(string.IsNullOrWhiteSpace(response.AccessToken));
        Assert.True(response.ExpiresAtUtc > DateTime.UtcNow);
        Assert.Equal(account.Id, response.User.Id);
        Assert.Empty(response.User.Roles);
        var membership = Assert.Single(response.User.OrganizerMemberships);
        Assert.Equal(organizerA.Id, membership.OrganizerId);
        Assert.Equal(OrganizerRole.RaceDirector, membership.Role);
    }

    [Fact]
    public async Task Login_WithInvalidPassword_IncrementsFailedAttemptCount()
    {
        var result = await CreateAuthController().Login(new AuthController.LoginRequest(account.Email!, "wrong password"), default);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(1, await users.GetAccessFailedCountAsync(account));
    }

    [Fact]
    public async Task Login_LocksAccountAfterFiveFailedAttempts()
    {
        var controller = CreateAuthController();
        IActionResult? result = null;
        for (var attempt = 0; attempt < 5; attempt++)
            result = await controller.Login(new AuthController.LoginRequest(account.Email!, "wrong password"), default);

        Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status423Locked, ((ObjectResult)result!).StatusCode);
        Assert.True(await users.IsLockedOutAsync(account));
        Assert.Contains(await db.AuditRecords.Select(x => x.Action).ToListAsync(), x => x == "Authentication.AccountLockedOut");

        var lockedAttempt = await controller.Login(new AuthController.LoginRequest(account.Email!, password), default);
        Assert.Equal(StatusCodes.Status423Locked, Assert.IsType<ObjectResult>(lockedAttempt).StatusCode);
    }

    [Fact]
    public async Task Login_AfterLockoutExpires_SucceedsAndResetsFailedAttempts()
    {
        var controller = CreateAuthController();
        for (var attempt = 0; attempt < 5; attempt++)
            await controller.Login(new AuthController.LoginRequest(account.Email!, "wrong password"), default);

        await users.SetLockoutEndDateAsync(account, DateTimeOffset.UtcNow.AddSeconds(-1));
        var result = await controller.Login(new AuthController.LoginRequest(account.Email!, password), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.False(await users.IsLockedOutAsync(account));
        Assert.Equal(0, await users.GetAccessFailedCountAsync(account));
    }

    [Fact]
    public async Task LogoutRevokesJwtAndCreatesSecurityAuditRecord()
    {
        var loginResult = await CreateAuthController().Login(new AuthController.LoginRequest(account.Email!, password), default);
        var login = Assert.IsType<AuthController.LoginResponse>(Assert.IsType<OkObjectResult>(loginResult).Value);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(login.AccessToken);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(jwt.Claims, "Bearer"))
        };
        var controller = new AuthController(users, signInManager, db,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = "unused-test-key-value-at-least-32-bytes",
                ["Jwt:Issuer"] = "RaceBib.Tests", ["Jwt:Audience"] = "RaceBib.Tests.Clients"
            }).Build(), new AuditLogger(db))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        Assert.IsType<NoContentResult>(await controller.Logout(default));
        Assert.Contains(await db.RevokedTokens.Select(x => x.JwtId).ToListAsync(), x => x == jwt.Id);
        Assert.Contains(await db.AuditRecords.Select(x => x.Action).ToListAsync(), x => x == "Authentication.JwtRevoked");
    }

    [Fact]
    public async Task Me_ReturnsActiveMembershipRolesAndOrganizerIdentity()
    {
        db.OrganizerMemberships.AddRange(
            new OrganizerMembership { OrganizerId = organizerA.Id, UserId = account.Id, Role = OrganizerRole.EventAdministrator },
            new OrganizerMembership { OrganizerId = organizerB.Id, UserId = account.Id, Role = OrganizerRole.TimingOfficial, IsActive = false });
        await db.SaveChangesAsync();

        var result = await CreateAuthController().Me(default);
        var profile = Assert.IsType<AuthController.UserProfile>(Assert.IsType<OkObjectResult>(result).Value);

        var membership = Assert.Single(profile.OrganizerMemberships);
        Assert.Equal(organizerA.Id, membership.OrganizerId);
        Assert.Equal(organizerA.Name, membership.OrganizerName);
        Assert.Equal(OrganizerRole.EventAdministrator, membership.Role);
    }

    [Fact]
    public async Task EventAdministrator_CanCreateEventForOwnOrganizer()
    {
        await AddMembership(organizerA, OrganizerRole.EventAdministrator);

        var result = await CreateEventsController(account).Create(CreateEventRequest(organizerA), default);

        Assert.IsType<CreatedAtActionResult>(result);
    }

    [Fact]
    public async Task RaceDirector_CanCreateEventForOwnOrganizer()
    {
        await AddMembership(organizerA, OrganizerRole.RaceDirector);

        var result = await CreateEventsController(account).Create(CreateEventRequest(organizerA), default);

        Assert.IsType<CreatedAtActionResult>(result);
    }

    [Fact]
    public async Task UserWithoutMembership_CannotCreateEvent()
    {
        var result = await CreateEventsController(account).Create(CreateEventRequest(organizerA), default);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task MembershipInOneOrganizer_CannotReadAnotherOrganizersEvent()
    {
        await AddMembership(organizerA, OrganizerRole.EventAdministrator);
        var item = new Event
        {
            OrganizerId = organizerB.Id,
            Name = "Coastal 10K",
            Slug = "coastal-10k",
            OrganizerName = organizerB.Name,
            StartDate = DateTime.UtcNow.AddDays(30),
            EndDate = DateTime.UtcNow.AddDays(30).AddHours(4),
            TimeZoneId = "Asia/Manila"
        };
        db.Events.Add(item);
        await db.SaveChangesAsync();

        var result = await CreateEventsController(account).Get(item.Id, default);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task MembershipInOneOrganizer_CannotListAnotherOrganizersRunners()
    {
        await AddMembership(organizerA, OrganizerRole.EventAdministrator);

        var result = await CreateRunnersController(account).List(organizerB.Id, null, 1, 20, default);

        Assert.IsType<ForbidResult>(result);
    }

    [Theory]
    [InlineData(OrganizerRole.RaceDirector, EventStatus.RegistrationClosed, 204)]
    [InlineData(OrganizerRole.RaceDirector, EventStatus.Ready, 409)]
    [InlineData(OrganizerRole.TimingOfficial, EventStatus.Draft, 403)]
    public async Task CourseStatusChangeRequiresManagementPermissionAndEditableLifecycle(
        OrganizerRole role, EventStatus eventStatus, int expectedStatus)
    {
        await AddMembership(organizerA, role);
        var item = new Event
        {
            OrganizerId = organizerA.Id, Name = "Course retirement", Slug = "course-retirement",
            OrganizerName = organizerA.Name, Status = eventStatus, TimeZoneId = "UTC",
            StartDate = DateTime.UtcNow, EndDate = DateTime.UtcNow.AddHours(1)
        };
        var course = new Course { EventId = item.Id, Name = "Route", DistanceKm = 10 };
        db.AddRange(item, course);
        await db.SaveChangesAsync();
        var controller = new RaceConfigurationController(db, services.GetRequiredService<IOrganizerMembershipAuthorization>())
        {
            ControllerContext = new ControllerContext { HttpContext = CreateHttpContext(account) }
        };
        var result = await controller.ChangeCourseStatus(item.Id, course.Id, new(RecordStatus.Inactive), default);

        var statusCode = result is ForbidResult ? 403 : ((Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult)result).StatusCode;
        Assert.Equal(expectedStatus, statusCode);
        Assert.Equal(expectedStatus == 204 ? RecordStatus.Inactive : RecordStatus.Active, course.Status);
    }

    private AuthController CreateAuthController()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)),
            ["Jwt:Issuer"] = "RaceBib.Tests",
            ["Jwt:Audience"] = "RaceBib.Tests.Clients",
            ["Jwt:AccessTokenMinutes"] = "15"
        }).Build();
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, account.Id)], "Test"));
        return new AuthController(users, signInManager, db, configuration, new RaceTiming.Api.Services.AuditLogger(db))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private EventsController CreateEventsController(ApplicationUser principalUser) => new(db,
        services.GetRequiredService<IOrganizerMembershipAuthorization>())
    {
        ControllerContext = new ControllerContext { HttpContext = CreateHttpContext(principalUser) }
    };

    private RunnersController CreateRunnersController(ApplicationUser principalUser) => new(db,
        services.GetRequiredService<IOrganizerMembershipAuthorization>())
    {
        ControllerContext = new ControllerContext { HttpContext = CreateHttpContext(principalUser) }
    };

    private static DefaultHttpContext CreateHttpContext(ApplicationUser principalUser)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, principalUser.Id)], "Test"));
        return context;
    }

    private Task AddMembership(Organizer organizer, OrganizerRole role)
    {
        db.OrganizerMemberships.Add(new OrganizerMembership
        {
            OrganizerId = organizer.Id,
            UserId = account.Id,
            Role = role
        });
        return db.SaveChangesAsync();
    }

    private static EventsController.CreateEventRequest CreateEventRequest(Organizer organizer) => new(
        organizer.Id, "North Trail 25K", "north-trail-25k", "A test event", organizer.Name,
        null, "Manila", DateTime.UtcNow.AddDays(30), DateTime.UtcNow.AddDays(30).AddHours(5), "Asia/Manila");
}
