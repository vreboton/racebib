using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Identity;
using RaceTiming.Infrastructure.Persistence;
using Xunit;

namespace RaceTiming.Api.Tests;

[CollectionDefinition("MySQL API integration", DisableParallelization = true)]
public sealed class MySqlApiIntegrationCollection : ICollectionFixture<MySqlApiIntegrationFixture>
{
    public const string Name = "MySQL API integration";
}

/// <summary>
/// Creates the API host only when a specifically named disposable test database is configured.
/// Ordinary unit-test runs leave this fixture inactive.
/// </summary>
public sealed class MySqlApiIntegrationFixture : IAsyncLifetime
{
    private bool createdSystemAdministratorRole;

    public MySqlApiFactory? Factory { get; private set; }

    public async Task InitializeAsync()
    {
        var configuredConnection = Environment.GetEnvironmentVariable(MySqlApiFactory.ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (MySqlApiFactory.IsRequiredInCurrentEnvironment)
                throw new InvalidOperationException($"{MySqlApiFactory.ConnectionEnvironmentVariable} is required when MySQL integration tests are required.");
            return;
        }

        var connectionString = MySqlApiFactory.ValidateTestConnection(configuredConnection);
        Factory = new MySqlApiFactory(connectionString);
        try
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
            var pendingMigrations = await db.Database.GetPendingMigrationsAsync();
            if (pendingMigrations.Any())
                throw new InvalidOperationException("The disposable MySQL test database has pending migrations. Apply committed migrations before running this suite.");

            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            if (!await roles.RoleExistsAsync("SystemAdministrator"))
            {
                var result = await roles.CreateAsync(new IdentityRole("SystemAdministrator"));
                if (!result.Succeeded)
                    throw new InvalidOperationException("Could not provision the integration-test platform role.");
                createdSystemAdministratorRole = true;
            }
        }
        catch
        {
            Factory.Dispose();
            Factory = null;
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (Factory is null)
            return;

        if (createdSystemAdministratorRole)
        {
            using var scope = Factory.Services.CreateScope();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var role = await roles.FindByNameAsync("SystemAdministrator");
            if (role is not null)
                await roles.DeleteAsync(role);
        }

        Factory.Dispose();
    }
}

public sealed class MySqlApiFactory : WebApplicationFactory<Program>
{
    public const string ConnectionEnvironmentVariable = "RACEBIB_MYSQL_TEST_CONNECTION";
    public const string RequiredEnvironmentVariable = "RACEBIB_MYSQL_TEST_REQUIRED";
    private readonly string connectionString;
    private readonly string jwtSigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    public static bool IsRequiredInCurrentEnvironment =>
        string.Equals(Environment.GetEnvironmentVariable(RequiredEnvironmentVariable), "true", StringComparison.OrdinalIgnoreCase);

    public MySqlApiFactory(string connectionString) => this.connectionString = ValidateTestConnection(connectionString);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString,
                ["Jwt:SigningKey"] = jwtSigningKey,
                ["Jwt:Issuer"] = "RaceBib.IntegrationTests",
                ["Jwt:Audience"] = "RaceBib.IntegrationTests",
                ["Jwt:AccessTokenMinutes"] = "15",
            }));

        return base.CreateHost(builder);
    }

    public static string ValidateTestConnection(string connection)
    {
        MySqlConnectionStringBuilder parsed;
        try
        {
            parsed = new MySqlConnectionStringBuilder(connection);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("MySQL integration tests require a valid dedicated test connection string.");
        }

        if (!parsed.Database.StartsWith("racebib_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MySQL integration tests require a dedicated database whose name starts with 'racebib_test_'.");

        if (!parsed.UserID.StartsWith("racebib_test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MySQL integration tests require a dedicated racebib_test database user.");

        if (!new[] { "localhost", "127.0.0.1", "::1" }.Contains(parsed.Server, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("MySQL integration tests only connect to a local disposable MySQL instance.");

        return connection;
    }
}

[Collection(MySqlApiIntegrationCollection.Name)]
public sealed partial class MySqlApiIntegrationTests(MySqlApiIntegrationFixture fixture)
{
    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task ValidTestAccountCanAuthenticateThroughTheJwtPipeline()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var client = Factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = account.Email,
            password = account.Password,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("accessToken").GetString()));
        Assert.Equal(account.Email, json.RootElement.GetProperty("user").GetProperty("email").GetString());
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task ProtectedPlatformEndpointRejectsUnauthenticatedRequest()
    {
        using var response = await Factory.CreateClient().GetAsync("/api/v1/platform/dashboard");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task OrganizerAdministratorCannotAccessPlatformAdministration()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));

        using var response = await client.GetAsync("/api/v1/platform/dashboard");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task OrganizerCannotReadAnotherOrganizersEventOrRunners()
    {
        await using var data = new MySqlTestData(Services);
        var accountA = await data.CreateAccountAsync();
        var organizerA = await data.CreateOrganizerAsync(accountA);
        var organizerB = await data.CreateOrganizerAsync();
        var eventA = await data.CreateEventAsync(organizerA);
        var eventB = await data.CreateEventAsync(organizerB);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, accountA));

        using var ownEvent = await client.GetAsync($"/api/v1/events/{eventA}");
        using var otherEvent = await client.GetAsync($"/api/v1/events/{eventB}");
        using var otherRunners = await client.GetAsync($"/api/v1/organizers/{organizerB}/runners");

        Assert.Equal(HttpStatusCode.OK, ownEvent.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherEvent.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherRunners.StatusCode);
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task SuspensionBlocksOrganizerAccessAndReactivationRestoresIt()
    {
        await using var data = new MySqlTestData(Services);
        var platformAdmin = await data.CreateAccountAsync(systemAdministrator: true);
        var member = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(member);
        using var adminClient = Factory.CreateClient();
        using var memberClient = Factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(adminClient, platformAdmin));
        memberClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(memberClient, member));

        using var beforeSuspend = await memberClient.GetAsync($"/api/v1/organizers/{organizerId}/runners");
        Assert.Equal(HttpStatusCode.OK, beforeSuspend.StatusCode);

        using var suspend = await adminClient.PutAsJsonAsync($"/api/v1/platform/organizers/{organizerId}/status", new { status = "Suspended" });
        Assert.Equal(HttpStatusCode.NoContent, suspend.StatusCode);
        using var suspendedAccess = await memberClient.GetAsync($"/api/v1/organizers/{organizerId}/runners");
        Assert.Equal(HttpStatusCode.Forbidden, suspendedAccess.StatusCode);

        using var reactivate = await adminClient.PutAsJsonAsync($"/api/v1/platform/organizers/{organizerId}/status", new { status = "Active" });
        Assert.Equal(HttpStatusCode.NoContent, reactivate.StatusCode);
        using var restoredAccess = await memberClient.GetAsync($"/api/v1/organizers/{organizerId}/runners");
        Assert.Equal(HttpStatusCode.OK, restoredAccess.StatusCode);
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task InvalidEventRequestReturnsProblemDetails()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));

        using var response = await client.PostAsJsonAsync("/api/v1/events", new
        {
            organizerId,
            name = "Integration Test Event",
            slug = "invalid--slug",
            description = (string?)null,
            organizerName = "Integration Test Organizer",
            venue = "Test Venue",
            location = "Test Location",
            startDateUtc = DateTime.UtcNow.AddDays(30),
            endDateUtc = DateTime.UtcNow.AddDays(30).AddHours(2),
            timeZoneId = "UTC",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Event slug must contain letters, numbers and single hyphens.", problem?.Title);
    }

    private IServiceProvider Services => Factory.Services;
    private MySqlApiFactory Factory => fixture.Factory ?? throw new InvalidOperationException("MySQL integration fixture is not active.");

    private static async Task<string> LoginAsync(HttpClient client, TestAccount account)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = account.Email, password = account.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = json.RootElement.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        return token!;
    }
}

public sealed class MySqlIntegrationFactAttribute : FactAttribute
{
    public MySqlIntegrationFactAttribute()
    {
        if (!MySqlApiFactory.IsRequiredInCurrentEnvironment &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MySqlApiFactory.ConnectionEnvironmentVariable)))
            Skip = $"Set {MySqlApiFactory.ConnectionEnvironmentVariable} to a local racebib_test_* MySQL database and racebib_test* user to run relational integration tests.";
    }
}

[Trait("Category", "MySqlSafety")]
public sealed class MySqlConnectionSafetyTests
{
    [Fact]
    public void WebApplicationFactorySuppliesStartupConfigurationBeforeProgramRuns()
    {
        const string connection = "Server=127.0.0.1;Port=3306;Database=racebib_test_ci;User=racebib_test;Password=test-only";
        using var factory = new MySqlApiFactory(connection);

        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        Assert.Equal(connection, configuration.GetConnectionString("DefaultConnection"));
        Assert.Equal("RaceBib.IntegrationTests", configuration["Jwt:Issuer"]);
        Assert.False(string.IsNullOrWhiteSpace(configuration["Jwt:SigningKey"]));
    }

    [Fact]
    public void AllowsOnlyTheDedicatedLocalTestTarget()
    {
        const string connection = "Server=127.0.0.1;Port=3306;Database=racebib_test_ci;User=racebib_test;Password=test-only";

        Assert.Equal(connection, MySqlApiFactory.ValidateTestConnection(connection));
    }

    [Theory]
    [InlineData("Server=db.example.com;Database=racebib_test_ci;User=racebib_test;Password=test-only")]
    [InlineData("Server=127.0.0.1;Database=racebib;User=racebib_test;Password=test-only")]
    [InlineData("Server=127.0.0.1;Database=racebib_test_ci;User=racebib;Password=test-only")]
    public void RejectsRemoteOrNonTestTargets(string connection)
    {
        Assert.Throws<InvalidOperationException>(() => MySqlApiFactory.ValidateTestConnection(connection));
    }
}

public sealed record TestAccount(string Id, string Email, string Password);

public sealed class MySqlTestData(IServiceProvider services) : IAsyncDisposable
{
    private readonly HashSet<string> userIds = [];
    private readonly HashSet<Guid> organizerIds = [];

    public async Task<TestAccount> CreateAccountAsync(bool systemAdministrator = false)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var id = Guid.NewGuid().ToString("N");
        var email = $"integration-{id}@racebib.test";
        var password = $"RaceBib-Test-{id[..10]}A1!";
        var user = new ApplicationUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            DisplayName = $"Integration User {id[..8]}",
            IsActive = true,
        };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
            throw new InvalidOperationException($"Could not create integration-test user (Identity codes: {string.Join(",", created.Errors.Select(error => error.Code))}).");

        userIds.Add(user.Id);
        if (systemAdministrator)
        {
            var assigned = await users.AddToRoleAsync(user, "SystemAdministrator");
            if (!assigned.Succeeded)
                throw new InvalidOperationException("Could not grant the integration-test platform role.");
        }
        return new TestAccount(user.Id, email, password);
    }

    public async Task<Guid> CreateOrganizerAsync(TestAccount? member = null, OrganizerRole role = OrganizerRole.EventAdministrator)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var id = Guid.NewGuid();
        var suffix = id.ToString("N");
        var organizer = new Organizer { Name = $"Integration Organizer {suffix[..8]}", Slug = $"integration-{suffix}" };
        db.Organizers.Add(organizer);
        if (member is not null)
            db.OrganizerMemberships.Add(new OrganizerMembership
            {
                OrganizerId = organizer.Id,
                UserId = member.Id,
                Role = role,
            });
        await db.SaveChangesAsync();
        organizerIds.Add(organizer.Id);
        return organizer.Id;
    }

    public async Task<Guid> CreateEventAsync(Guid organizerId)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var suffix = Guid.NewGuid().ToString("N");
        var start = DateTime.UtcNow.AddDays(30);
        var item = new Event
        {
            OrganizerId = organizerId,
            Name = $"Integration Event {suffix[..8]}",
            Slug = $"integration-event-{suffix}",
            OrganizerName = "Integration Organizer",
            StartDate = start,
            EndDate = start.AddHours(2),
            TimeZoneId = "UTC",
        };
        db.Events.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    public async ValueTask DisposeAsync()
    {
        if (userIds.Count == 0 && organizerIds.Count == 0)
            return;

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var userIdList = userIds.ToArray();
        var organizerIdList = organizerIds.ToArray();

        var auditRecords = await db.AuditRecords.Where(record =>
            (record.ActorUserId != null && userIdList.Contains(record.ActorUserId)) ||
            (record.OrganizerId.HasValue && organizerIdList.Contains(record.OrganizerId.Value))).ToListAsync();
        db.AuditRecords.RemoveRange(auditRecords);
        db.Registrations.RemoveRange(await db.Registrations.Where(item => organizerIdList.Contains(item.OrganizerId)).ToListAsync());
        db.Events.RemoveRange(await db.Events.Where(item => organizerIdList.Contains(item.OrganizerId)).ToListAsync());
        db.Runners.RemoveRange(await db.Runners.Where(item => organizerIdList.Contains(item.OrganizerId)).ToListAsync());
        db.OrganizerMemberships.RemoveRange(await db.OrganizerMemberships.Where(item => organizerIdList.Contains(item.OrganizerId)).ToListAsync());
        db.Organizers.RemoveRange(await db.Organizers.Where(item => organizerIdList.Contains(item.Id)).ToListAsync());
        await db.SaveChangesAsync();

        foreach (var userId in userIdList)
        {
            var user = await users.FindByIdAsync(userId);
            if (user is not null)
                await users.DeleteAsync(user);
        }
    }
}
