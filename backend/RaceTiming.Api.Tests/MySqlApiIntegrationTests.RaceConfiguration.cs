using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Persistence;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed partial class MySqlApiIntegrationTests
{
    private sealed record RoutePoint(Guid? Id, string Name, string Code, int Sequence, decimal DistanceFromStartKm,
        bool IsStart = false, bool IsFinish = false);

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task CourseReorderAndClonePreserveCheckpointIdentityAndRollbackOnConflict()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizerId);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));

        var path = $"/api/v1/events/{eventId}";
        var courseId = await CreatedId(await client.PostAsJsonAsync(path + "/courses", new { name = "Trail", distanceKm = 10 }));
        using var precision = await client.PostAsJsonAsync(path + "/courses", new { name = "Invalid", distanceKm = 10.0001m });
        Assert.Equal(HttpStatusCode.BadRequest, precision.StatusCode);
        using var duplicate = await client.PostAsJsonAsync(path + "/courses", new { name = " trail ", distanceKm = 10 });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var routePath = $"{path}/courses/{courseId}/checkpoints";
        var points = new[]
        {
            new RoutePoint(null, "Start", "S", 1, 0, IsStart: true),
            new RoutePoint(null, "Water", "A", 2, 5),
            new RoutePoint(null, "Aid", "B", 3, 5),
            new RoutePoint(null, "Finish", "F", 4, 10, IsFinish: true)
        };
        using var initial = await client.PutAsJsonAsync(routePath, new { distanceKm = 10, checkpoints = points });
        Assert.Equal(HttpStatusCode.NoContent, initial.StatusCode);
        var config = await client.GetFromJsonAsync<JsonElement>(path + "/configuration");
        var saved = config.GetProperty("courses")[0].GetProperty("checkpoints");
        points = points.Select((point, index) => point with { Id = saved[index].GetProperty("id").GetGuid() }).ToArray();
        points[1] = points[1] with { Sequence = 3, Code = "B" };
        points[2] = points[2] with { Sequence = 2, Code = "A" };
        using var reorder = await client.PutAsJsonAsync(routePath, new { distanceKm = 10, checkpoints = points });
        Assert.Equal(HttpStatusCode.NoContent, reorder.StatusCode);
        var invalid = points.Select(p => p.Sequence == 3 ? p with { Code = "Á" } : p).ToArray();
        using var rejected = await client.PutAsJsonAsync(routePath, new { distanceKm = 10, checkpoints = invalid });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        var clonePath = $"{path}/courses/{courseId}/revisions";
        var clones = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => client.PostAsync(clonePath, null)));
        foreach (var response in clones)
        {
            using (response) Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        }
        Assert.Contains(clones, response => response.StatusCode == HttpStatusCode.OK);
        config = await client.GetFromJsonAsync<JsonElement>(path + "/configuration");
        var courses = config.GetProperty("courses");
        Assert.InRange(courses.GetArrayLength(), 2, 3);
        Assert.Equal(Enumerable.Range(1, courses.GetArrayLength()),
            courses.EnumerateArray().Select(course => course.GetProperty("version").GetInt32()));
        var historical = courses[0].GetProperty("checkpoints");
        Assert.Equal(points[2].Id, historical[1].GetProperty("id").GetGuid());
        Assert.Equal("B", historical[2].GetProperty("code").GetString());
        Assert.NotEqual(historical[1].GetProperty("id").GetGuid(),
            courses[1].GetProperty("checkpoints")[1].GetProperty("id").GetGuid());
        using var locked = await client.PutAsJsonAsync(routePath, new { distanceKm = 10, checkpoints = points });
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task RaceConfigurationRejectsCrossScopeAndScheduleViolationsAndKeepsRetiredRecords()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account);
        var eventId = await data.CreateEventAsync(organizerId);
        var otherEventId = await data.CreateEventAsync(organizerId);
        var foreignEventId = await data.CreateEventAsync(await data.CreateOrganizerAsync());
        using var client = Factory.CreateClient();
        using var anonymous = Factory.CreateClient();
        var path = $"/api/v1/events/{eventId}";
        using var unauthorized = await anonymous.GetAsync(path + "/configuration");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));
        using var forbidden = await client.GetAsync($"/api/v1/events/{foreignEventId}/configuration");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var courseId = await CreatedId(await client.PostAsJsonAsync(path + "/courses", new { name = "Trail", distanceKm = 10 }));
        var points = new[]
        {
            new RoutePoint(null, "Start", "S", 1, 0, IsStart: true),
            new RoutePoint(null, "Finish", "F", 2, 10, IsFinish: true)
        };
        using var route = await client.PutAsJsonAsync($"{path}/courses/{courseId}/checkpoints",
            new { distanceKm = 10, checkpoints = points });
        Assert.Equal(HttpStatusCode.NoContent, route.StatusCode);
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var item = await db.Events.SingleAsync(x => x.Id == eventId);
        var startAt = new DateTimeOffset(DateTime.SpecifyKind(item.StartDate.AddMinutes(10), DateTimeKind.Utc));
        var raceRequest = new { courseId, name = "10K", startAt = startAt.ToString("yyyy-MM-ddTHH:mm:sszzz") };
        using var mismatched = await client.PostAsJsonAsync($"/api/v1/events/{otherEventId}/races",
            raceRequest with { startAt = DateTimeOffset.UtcNow.AddDays(30).AddMinutes(10).ToString("yyyy-MM-ddTHH:mm:sszzz") });
        Assert.Equal(HttpStatusCode.NotFound, mismatched.StatusCode);
        using var naive = await client.PostAsJsonAsync(path + "/races", raceRequest with { startAt = "2030-01-01T08:00" });
        Assert.Equal(HttpStatusCode.BadRequest, naive.StatusCode);
        var raceId = await CreatedId(await client.PostAsJsonAsync(path + "/races", raceRequest));
        var racePath = $"{path}/races/{raceId}";
        using var assignedCourse = await client.PutAsJsonAsync($"{path}/courses/{courseId}/checkpoints",
            new { distanceKm = 10, checkpoints = points });
        Assert.Equal(HttpStatusCode.Conflict, assignedCourse.StatusCode);
        var revisionId = await CreatedId(await client.PostAsync($"{path}/courses/{courseId}/revisions", null));
        using var invalidAges = await client.PostAsJsonAsync(racePath + "/categories",
            new { name = "Masters", minimumAge = 60, maximumAge = 40 });
        Assert.Equal(HttpStatusCode.BadRequest, invalidAges.StatusCode);
        var categoryId = await CreatedId(await client.PostAsJsonAsync(racePath + "/categories",
            new { name = "Masters", minimumAge = 40, maximumAge = 60 }));

        static string At(DateTimeOffset time) => time.ToString("yyyy-MM-ddTHH:mm:sszzz");
        var waveId = await CreatedId(await client.PostAsJsonAsync(racePath + "/waves",
            new { name = "First", sequence = 1, startAt = At(startAt.AddMinutes(10)), capacity = 30 }));
        using var outOfOrder = await client.PostAsJsonAsync(racePath + "/waves",
            new { name = "Second", sequence = 2, startAt = At(startAt.AddMinutes(5)) });
        Assert.Equal(HttpStatusCode.BadRequest, outOfOrder.StatusCode);
        using var lateRace = await client.PutAsJsonAsync(racePath, raceRequest with { startAt = At(startAt.AddMinutes(20)) });
        Assert.Equal(HttpStatusCode.BadRequest, lateRace.StatusCode);
        using var invalidWindow = await client.PutAsJsonAsync(path, new
        {
            item.Name, item.Slug, item.OrganizerName, item.TimeZoneId,
            startDateUtc = item.StartDate, endDateUtc = startAt.UtcDateTime.AddMinutes(5)
        });
        Assert.Equal(HttpStatusCode.Conflict, invalidWindow.StatusCode);

        var runner = new Runner { OrganizerId = organizerId, FirstName = "Synthetic", LastName = "Runner" };
        var registration = new Registration
        {
            EventId = eventId, OrganizerId = organizerId, RaceId = raceId, Runner = runner,
            RaceCategoryId = categoryId, StartWaveId = waveId, BibNumber = "12"
        };
        db.Registrations.Add(registration);
        await db.SaveChangesAsync();
        using var registeredSwitch = await client.PutAsJsonAsync(racePath, raceRequest with { courseId = revisionId });
        Assert.Equal(HttpStatusCode.Conflict, registeredSwitch.StatusCode);

        using var retireCategory = await client.DeleteAsync($"{racePath}/categories/{categoryId}");
        using var retireWave = await client.DeleteAsync($"{racePath}/waves/{waveId}");
        using var retireRace = await client.DeleteAsync(racePath);
        Assert.Equal(HttpStatusCode.NoContent, retireCategory.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, retireWave.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, retireRace.StatusCode);
        var saved = await client.GetFromJsonAsync<JsonElement>(racePath);
        Assert.Equal("Inactive", saved.GetProperty("status").GetString());
        Assert.Equal(categoryId, saved.GetProperty("categories")[0].GetProperty("id").GetGuid());
        Assert.Equal(waveId, saved.GetProperty("waves")[0].GetProperty("id").GetGuid());
        Assert.Equal(courseId, saved.GetProperty("courseId").GetGuid());
        var storedRegistration = await db.Registrations.AsNoTracking().SingleAsync(x => x.Id == registration.Id);
        Assert.Equal(categoryId, storedRegistration.RaceCategoryId);
        Assert.Equal(waveId, storedRegistration.StartWaveId);

        var membership = await db.OrganizerMemberships.SingleAsync(x => x.OrganizerId == organizerId && x.UserId == account.Id);
        membership.IsActive = false;
        await db.SaveChangesAsync();
        using var inactive = await client.GetAsync(path + "/configuration");
        Assert.Equal(HttpStatusCode.Forbidden, inactive.StatusCode);
        membership.IsActive = true;
        var organizer = await db.Organizers.SingleAsync(x => x.Id == organizerId);
        organizer.IsSuspended = true;
        await db.SaveChangesAsync();
        using var suspended = await client.PostAsJsonAsync(path + "/courses", new { name = "Blocked", distanceKm = 10 });
        Assert.Equal(HttpStatusCode.Forbidden, suspended.StatusCode);
        organizer.IsSuspended = false;

        item.Status = EventStatus.RegistrationClosed;
        await db.SaveChangesAsync();
        using var incomplete = await client.PatchAsJsonAsync(path + "/status", new { status = "Ready" });
        Assert.Equal(HttpStatusCode.Conflict, incomplete.StatusCode);
        await CreatedId(await client.PostAsJsonAsync(path + "/races", raceRequest with { courseId = revisionId, name = "Replacement" }));
        using var ready = await client.PatchAsJsonAsync(path + "/status", new { status = "Ready" });
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var locked = await client.PostAsJsonAsync(path + "/courses", new { name = "Blocked", distanceKm = 10 });
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
    }

    private static async Task<Guid> CreatedId(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }
    }
}
