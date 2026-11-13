using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace RaceTiming.Api.Tests;

/// <summary>
/// Opt-in test for the running Docker API and its configured relational database.
/// Supply values through process environment variables; never commit credentials or tokens.
/// </summary>
public sealed class LiveDockerIntegrationTests
{
    [LiveDockerFact]
    [Trait("Category", "LiveDocker")]
    public async Task LifecycleAndPlatformAuthorizationUseTheLiveHttpPipeline()
    {
        var baseUrl = Environment.GetEnvironmentVariable("RACEBIB_INTEGRATION_BASE_URL");
        var adminEmail = Environment.GetEnvironmentVariable("RACEBIB_INTEGRATION_ADMIN_EMAIL");
        var adminPassword = Environment.GetEnvironmentVariable("RACEBIB_INTEGRATION_ADMIN_PASSWORD");
        var organizerId = Environment.GetEnvironmentVariable("RACEBIB_INTEGRATION_ORGANIZER_ID");
        var memberId = Environment.GetEnvironmentVariable("RACEBIB_INTEGRATION_MEMBER_ID");
        var memberToken = Environment.GetEnvironmentVariable("RACEBIB_INTEGRATION_MEMBER_TOKEN");
        var eventId = Environment.GetEnvironmentVariable("RACEBIB_INTEGRATION_EVENT_ID");
        var otherOrganizerEventId = Environment.GetEnvironmentVariable("RACEBIB_INTEGRATION_OTHER_ORGANIZER_EVENT_ID");
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl!) };
        using var login = await client.PostAsJsonAsync("api/v1/auth/login", new { email = adminEmail, password = adminPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var adminToken = loginJson.RootElement.GetProperty("accessToken").GetString()
            ?? throw new InvalidOperationException("The live API returned an empty access token.");
        Assert.False(string.IsNullOrWhiteSpace(adminToken));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var dashboard = await client.GetAsync("api/v1/platform/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        using var organizerResponse = await client.GetAsync($"api/v1/platform/organizers/{organizerId}");
        Assert.Equal(HttpStatusCode.OK, organizerResponse.StatusCode);
        using var organizerJson = JsonDocument.Parse(await organizerResponse.Content.ReadAsStringAsync());
        var baselineMembers = organizerJson.RootElement.GetProperty("memberCount").GetInt32();
        var baselineEvents = organizerJson.RootElement.GetProperty("eventCount").GetInt32();

        using var membersResponse = await client.GetAsync($"api/v1/organizers/{organizerId}/members");
        Assert.Equal(HttpStatusCode.OK, membersResponse.StatusCode);
        using var membersJson = JsonDocument.Parse(await membersResponse.Content.ReadAsStringAsync());
        var member = membersJson.RootElement.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("userId").GetString() == memberId);
        var membershipId = member.GetProperty("membershipId").GetGuid();
        var originalMembershipRole = member.GetProperty("role").GetString();
        Assert.Equal("EventAdministrator", originalMembershipRole);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
        using var deniedDashboard = await client.GetAsync("api/v1/platform/dashboard");
        Assert.Equal(HttpStatusCode.Forbidden, deniedDashboard.StatusCode);
        using var memberEvent = await client.GetAsync($"api/v1/events/{eventId}");
        Assert.Equal(HttpStatusCode.OK, memberEvent.StatusCode);
        using var memberRunners = await client.GetAsync($"api/v1/organizers/{organizerId}/runners");
        Assert.Equal(HttpStatusCode.OK, memberRunners.StatusCode);
        if (!string.IsNullOrWhiteSpace(otherOrganizerEventId))
        {
            using var crossOrganizerEvent = await client.GetAsync($"api/v1/events/{otherOrganizerEventId}");
            Assert.Equal(HttpStatusCode.Forbidden, crossOrganizerEvent.StatusCode);
        }

        try
        {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var grantAdmin = await client.PutAsJsonAsync($"api/v1/platform/users/{memberId}/platform-admin", new { isAdministrator = true });
        Assert.Equal(HttpStatusCode.NoContent, grantAdmin.StatusCode);
        using var revokeAdmin = await client.PutAsJsonAsync($"api/v1/platform/users/{memberId}/platform-admin", new { isAdministrator = false });
        Assert.Equal(HttpStatusCode.NoContent, revokeAdmin.StatusCode);
        using var changeRole = await client.PutAsJsonAsync($"api/v1/organizers/{organizerId}/members/{membershipId}/role", new { role = "RaceDirector" });
        Assert.Equal(HttpStatusCode.NoContent, changeRole.StatusCode);
        using var restoreRole = await client.PutAsJsonAsync($"api/v1/organizers/{organizerId}/members/{membershipId}/role", new { role = originalMembershipRole });
        Assert.Equal(HttpStatusCode.NoContent, restoreRole.StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            await SetOrganizerStatus(client, organizerId!, "Suspended");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
            using var suspendedEvent = await client.GetAsync($"api/v1/events/{eventId}");
            using var suspendedRunners = await client.GetAsync($"api/v1/organizers/{organizerId}/runners");
            Assert.Equal(HttpStatusCode.Forbidden, suspendedEvent.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, suspendedRunners.StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            await SetOrganizerStatus(client, organizerId!, "Active");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
            using var restoredEvent = await client.GetAsync($"api/v1/events/{eventId}");
            Assert.Equal(HttpStatusCode.OK, restoredEvent.StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            await SetOrganizerStatus(client, organizerId!, "Inactive");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
            using var inactiveEvent = await client.GetAsync($"api/v1/events/{eventId}");
            Assert.Equal(HttpStatusCode.Forbidden, inactiveEvent.StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            await SetOrganizerStatus(client, organizerId!, "Active");
            using var audit = await client.GetAsync($"api/v1/platform/audit?search={Uri.EscapeDataString(organizerId!)}&page=1&pageSize=100");
            Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
            var auditText = await audit.Content.ReadAsStringAsync();
            Assert.Contains("Platform.OrganizerSuspended", auditText, StringComparison.Ordinal);
            Assert.Contains("Platform.OrganizerDeactivated", auditText, StringComparison.Ordinal);
            Assert.Contains("Platform.OrganizerReactivated", auditText, StringComparison.Ordinal);

            using var adminActions = await client.GetAsync($"api/v1/platform/audit?search={Uri.EscapeDataString(memberId!)}&page=1&pageSize=100");
            Assert.Equal(HttpStatusCode.OK, adminActions.StatusCode);
            var adminAuditText = await adminActions.Content.ReadAsStringAsync();
            Assert.Contains("Platform.AdministratorAssigned", adminAuditText, StringComparison.Ordinal);
            Assert.Contains("Platform.AdministratorRevoked", adminAuditText, StringComparison.Ordinal);
            using var roleActions = await client.GetAsync("api/v1/platform/audit?action=Organizer.MembershipRoleChanged&page=1&pageSize=100");
            Assert.Equal(HttpStatusCode.OK, roleActions.StatusCode);
            Assert.Contains("Organizer.MembershipRoleChanged", await roleActions.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.DoesNotContain(adminToken, auditText, StringComparison.Ordinal);
            Assert.DoesNotContain(memberToken!, auditText, StringComparison.Ordinal);
            Assert.DoesNotContain(adminPassword!, auditText, StringComparison.Ordinal);

            using var forbiddenMutation = await client.PostAsJsonAsync("api/v1/platform/audit", new { action = "tamper" });
            using var forbiddenUpdate = await client.PutAsJsonAsync("api/v1/platform/audit", new { action = "tamper" });
            using var forbiddenDelete = await client.DeleteAsync("api/v1/platform/audit");
            Assert.Equal(HttpStatusCode.MethodNotAllowed, forbiddenMutation.StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, forbiddenUpdate.StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, forbiddenDelete.StatusCode);

            using var finalOrganizer = await client.GetAsync($"api/v1/platform/organizers/{organizerId}");
            using var finalJson = JsonDocument.Parse(await finalOrganizer.Content.ReadAsStringAsync());
            Assert.Equal("Active", finalJson.RootElement.GetProperty("status").GetString());
            Assert.Equal(baselineMembers, finalJson.RootElement.GetProperty("memberCount").GetInt32());
            Assert.Equal(baselineEvents, finalJson.RootElement.GetProperty("eventCount").GetInt32());
        }
        finally
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            using var roleRestore = await client.PutAsJsonAsync($"api/v1/organizers/{organizerId}/members/{membershipId}/role", new { role = originalMembershipRole });
            Assert.Equal(HttpStatusCode.NoContent, roleRestore.StatusCode);
            using var revokeAdmin = await client.PutAsJsonAsync($"api/v1/platform/users/{memberId}/platform-admin", new { isAdministrator = false });
            Assert.Equal(HttpStatusCode.NoContent, revokeAdmin.StatusCode);
            using var restore = await client.PutAsJsonAsync($"api/v1/platform/organizers/{organizerId}/status", new { status = "Active" });
            Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);
        }

        using var logout = await client.PostAsync("api/v1/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var revokedMe = await client.GetAsync("api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, revokedMe.StatusCode);
        using var secondLogin = await client.PostAsJsonAsync("api/v1/auth/login", new { email = adminEmail, password = adminPassword });
        Assert.Equal(HttpStatusCode.OK, secondLogin.StatusCode);
        using var secondLoginJson = JsonDocument.Parse(await secondLogin.Content.ReadAsStringAsync());
        var secondAdminToken = secondLoginJson.RootElement.GetProperty("accessToken").GetString()
            ?? throw new InvalidOperationException("The live API returned an empty re-login token.");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondAdminToken);
        using var revocationAudit = await client.GetAsync("api/v1/platform/audit?action=Authentication.JwtRevoked&page=1&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, revocationAudit.StatusCode);
        Assert.Contains("Authentication.JwtRevoked", await revocationAudit.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task SetOrganizerStatus(HttpClient client, string organizerId, string status)
    {
        using var response = await client.PutAsJsonAsync($"api/v1/platform/organizers/{organizerId}/status", new { status });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}

public sealed class LiveDockerFactAttribute : FactAttribute
{
    public LiveDockerFactAttribute()
    {
        var required = new[]
        {
            "RACEBIB_INTEGRATION_BASE_URL", "RACEBIB_INTEGRATION_ADMIN_EMAIL",
            "RACEBIB_INTEGRATION_ADMIN_PASSWORD", "RACEBIB_INTEGRATION_ORGANIZER_ID",
            "RACEBIB_INTEGRATION_MEMBER_ID", "RACEBIB_INTEGRATION_MEMBER_TOKEN",
            "RACEBIB_INTEGRATION_EVENT_ID"
        };
        if (required.Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            Skip = "Set RACEBIB_INTEGRATION_* variables to run against the existing Docker API and relational test data.";
    }
}
