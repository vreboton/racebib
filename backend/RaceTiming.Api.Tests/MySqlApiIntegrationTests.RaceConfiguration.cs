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

        var coursePath = $"{path}/courses/{courseId}";
        using var caseOnlyRename = await client.PutAsJsonAsync(coursePath, new { name = "TRAIL", distanceKm = 10 });
        Assert.Equal(HttpStatusCode.OK, caseOnlyRename.StatusCode);
        var routePath = coursePath + "/checkpoints";
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

        var latest = courses[courses.GetArrayLength() - 1].GetProperty("id").GetGuid();
        using var versionedRename = await client.PutAsJsonAsync($"{path}/courses/{latest}", new { name = "Trail", distanceKm = 10 });
        Assert.Equal(HttpStatusCode.Conflict, versionedRename.StatusCode);

        // Older records can spell the same MySQL revision family differently.
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var storedLatest = await db.Courses.SingleAsync(x => x.Id == latest);
        storedLatest.Name = "trail";
        await db.SaveChangesAsync();
        var start = await db.Events.Where(x => x.Id == eventId).Select(x => x.StartDate).SingleAsync();
        var request = new { courseId, name = "Revision switch", startAt = start.AddMinutes(10).ToString("yyyy-MM-ddTHH:mm:ss") + "Z" };
        var raceId = await CreatedId(await client.PostAsJsonAsync(path + "/races", request));
        var racePath = $"{path}/races/{raceId}";
        var before = await client.GetFromJsonAsync<JsonElement>(path + "/configuration");
        Assert.Contains(latest, before.GetProperty("races")[0].GetProperty("revisionIds").EnumerateArray().Select(x => x.GetGuid()));
        using var switched = await client.PutAsJsonAsync(racePath, request with { courseId = latest });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>(racePath);
        Assert.Equal(latest, after.GetProperty("courseId").GetGuid());
        using var downgrade = await client.PutAsJsonAsync(racePath, request);
        Assert.Equal(HttpStatusCode.BadRequest, downgrade.StatusCode);
        var legacyCopyId = await CreatedId(await client.PostAsync($"{coursePath}/duplicate", null));
        var legacyCopy = (await client.GetFromJsonAsync<JsonElement>(path + "/configuration")).GetProperty("courses")
            .EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == legacyCopyId);
        Assert.Equal(1, legacyCopy.GetProperty("version").GetInt32());
        Assert.Equal("TRAIL (Copy)", legacyCopy.GetProperty("name").GetString());
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
        using var incompleteAssignedCourse = await client.PutAsJsonAsync($"{path}/courses/{courseId}/checkpoints",
            new { distanceKm = 10, checkpoints = points.Take(1) });
        Assert.Equal(HttpStatusCode.BadRequest, incompleteAssignedCourse.StatusCode);
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

        var categoryPath = $"{racePath}/categories/{categoryId}";
        var wavePath = $"{racePath}/waves/{waveId}";
        using var editCategory = await client.PutAsJsonAsync(categoryPath, new { name = "Masters", minimumAge = 45, maximumAge = 65 });
        using var editWave = await client.PutAsJsonAsync(wavePath,
            new { name = "First", sequence = 1, startAt = At(startAt.AddMinutes(10)), capacity = 25 });
        Assert.Equal(HttpStatusCode.OK, editCategory.StatusCode);
        Assert.Equal(HttpStatusCode.OK, editWave.StatusCode);

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

        var categorySnapshot = new { name = "Masters", description = (string?)null, minimumAge = 45, maximumAge = 65 };
        var waveSnapshot = new { name = "First", sequence = 1, startAt = At(startAt.AddMinutes(10)), capacity = 25 };
        foreach (var change in new[]
        {
            categorySnapshot with { name = "Changed" },
            categorySnapshot with { description = "Changed" },
            categorySnapshot with { minimumAge = 18 },
            categorySnapshot with { maximumAge = 70 }
        })
        {
            using var response = await client.PutAsJsonAsync(categoryPath, change);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        foreach (var change in new[]
        {
            waveSnapshot with { name = "Changed" },
            waveSnapshot with { sequence = 2 },
            waveSnapshot with { startAt = At(startAt.AddMinutes(20)) },
            waveSnapshot with { capacity = 99 }
        })
        {
            using var response = await client.PutAsJsonAsync(wavePath, change);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        var linked = await client.GetFromJsonAsync<JsonElement>(path + "/configuration");
        var linkedRace = linked.GetProperty("races")[0];
        Assert.Empty(linkedRace.GetProperty("revisionIds").EnumerateArray());
        Assert.True(linkedRace.GetProperty("categories")[0].GetProperty("hasRegistrations").GetBoolean());
        Assert.True(linkedRace.GetProperty("waves")[0].GetProperty("hasRegistrations").GetBoolean());

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
        var savedCategory = saved.GetProperty("categories")[0];
        Assert.Equal("Masters", savedCategory.GetProperty("name").GetString());
        Assert.Equal(45, savedCategory.GetProperty("minimumAge").GetInt32());
        Assert.Equal(65, savedCategory.GetProperty("maximumAge").GetInt32());
        var savedWave = saved.GetProperty("waves")[0];
        Assert.Equal("First", savedWave.GetProperty("name").GetString());
        Assert.Equal(1, savedWave.GetProperty("sequence").GetInt32());
        Assert.Equal(25, savedWave.GetProperty("capacity").GetInt32());
        Assert.Equal(DateTimeOffset.Parse(At(startAt.AddMinutes(10))), savedWave.GetProperty("startAt").GetDateTimeOffset());
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
        var replacementId = await CreatedId(await client.PostAsJsonAsync(path + "/races",
            raceRequest with { courseId = revisionId, name = "Replacement" }));
        var replacementWaves = $"{path}/races/{replacementId}/waves";
        var waveRequest = new { name = "Open capacity", sequence = 3, startAt = At(startAt.AddMinutes(10)), capacity = (int?)null };
        await CreatedId(await client.PostAsJsonAsync(replacementWaves, waveRequest));
        using var duplicateSequence = await client.PostAsJsonAsync(replacementWaves, waveRequest with { name = "Duplicate" });
        using var zeroCapacity = await client.PostAsJsonAsync(replacementWaves, waveRequest with { sequence = 4, capacity = 0 });
        Assert.Equal(HttpStatusCode.Conflict, duplicateSequence.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, zeroCapacity.StatusCode);
        using var ready = await client.PatchAsJsonAsync(path + "/status", new { status = "Ready" });
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var locked = await client.PostAsJsonAsync(path + "/courses", new { name = "Blocked", distanceKm = 10 });
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task ReadyAllowsLegacyEventsButRejectsEventsWithOnlyCourses()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizer = await data.CreateOrganizerAsync(account);
        var legacy = await data.CreateEventAsync(organizer);
        var courseOnly = await data.CreateEventAsync(organizer);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        foreach (var item in await db.Events.Where(x => x.Id == legacy || x.Id == courseOnly).ToListAsync())
            item.Status = EventStatus.RegistrationClosed;
        await db.SaveChangesAsync();

        await CreatedId(await client.PostAsJsonAsync($"/api/v1/events/{courseOnly}/courses", new { name = "Draft course", distanceKm = 10 }));
        using var allowed = await client.PatchAsJsonAsync($"/api/v1/events/{legacy}/status", new { status = "Ready" });
        using var blocked = await client.PatchAsJsonAsync($"/api/v1/events/{courseOnly}/status", new { status = "Ready" });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(EventStatus.RegistrationClosed, await db.Events.Where(x => x.Id == courseOnly).Select(x => x.Status).SingleAsync());
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task DuplicateCourseIsIndependentAndAssignedCourseEditsRespectRegistrationHistory()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizerId);
        var siblingEvent = await data.CreateEventAsync(organizerId);
        var foreignEvent = await data.CreateEventAsync(await data.CreateOrganizerAsync());
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));
        var path = $"/api/v1/events/{eventId}";

        var metadata = new { name = "Summit", description = "Trail route", distanceKm = 10, elevationGainMeters = 800 };
        var courseId = await CreatedId(await client.PostAsJsonAsync(path + "/courses", metadata));
        var coursePath = $"{path}/courses/{courseId}";
        var duplicatePath = coursePath + "/duplicate";
        using var anonymous = Factory.CreateClient();
        using var denied = await anonymous.PostAsync(duplicatePath, null);
        using var foreign = await client.PostAsync($"/api/v1/events/{foreignEvent}/courses/{courseId}/duplicate", null);
        using var sibling = await client.PostAsync($"/api/v1/events/{siblingEvent}/courses/{courseId}/duplicate", null);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, sibling.StatusCode);
        await CreatedId(await client.PostAsJsonAsync(path + "/courses", metadata with { name = "SUMMIT (copy)" }));

        var points = new[]
        {
            new { id = (Guid?)null, name = "Start", code = "S", sequence = 1, distanceFromStartKm = 0,
                isStart = true, isFinish = false, elevationMeters = 100, isMandatory = true, hasCutoff = false,
                cutoffDurationSeconds = (int?)null, description = "Start gate" },
            new { id = (Guid?)null, name = "Water", code = "A", sequence = 2, distanceFromStartKm = 5,
                isStart = false, isFinish = false, elevationMeters = 400, isMandatory = true, hasCutoff = true,
                cutoffDurationSeconds = (int?)3600, description = "Aid station" },
            new { id = (Guid?)null, name = "Aid", code = "B", sequence = 3, distanceFromStartKm = 5,
                isStart = false, isFinish = false, elevationMeters = 420, isMandatory = false, hasCutoff = false,
                cutoffDurationSeconds = (int?)null, description = "Drop bag" },
            new { id = (Guid?)null, name = "Finish", code = "F", sequence = 4, distanceFromStartKm = 10,
                isStart = false, isFinish = true, elevationMeters = 200, isMandatory = true, hasCutoff = false,
                cutoffDurationSeconds = (int?)null, description = "Finish gate" },
        };
        using var route = await client.PutAsJsonAsync(coursePath + "/checkpoints", new { distanceKm = 10, checkpoints = points });
        Assert.Equal(HttpStatusCode.NoContent, route.StatusCode);
        async Task<JsonElement> Configuration() => await client.GetFromJsonAsync<JsonElement>(path + "/configuration");
        static JsonElement Course(JsonElement config, Guid id) => config.GetProperty("courses").EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == id);
        var original = Course(await Configuration(), courseId);

        var duplicates = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ => await CreatedId(await client.PostAsync(duplicatePath, null))));
        var copied = await Configuration();
        Assert.Equal(new[] { "Summit (Copy 2)", "Summit (Copy 3)" },
            duplicates.Select(id => Course(copied, id).GetProperty("name").GetString()).Order());
        Assert.Equal(2, duplicates.Distinct().Count());
        var duplicate = Course(copied, duplicates[0]);
        Assert.Equal(1, duplicate.GetProperty("version").GetInt32());
        Assert.Equal(800, duplicate.GetProperty("elevationGainMeters").GetInt32());
        Assert.Equal("Trail route", duplicate.GetProperty("description").GetString());
        Assert.Equal(10m, duplicate.GetProperty("distanceKm").GetDecimal());
        foreach (var (source, copy) in original.GetProperty("checkpoints").EnumerateArray().Zip(duplicate.GetProperty("checkpoints").EnumerateArray()))
        {
            Assert.NotEqual(source.GetProperty("id").GetGuid(), copy.GetProperty("id").GetGuid());
            foreach (var field in source.EnumerateObject().Where(x => x.Name != "id"))
                Assert.Equal(field.Value.GetRawText(), copy.GetProperty(field.Name).GetRawText());
        }
        using var renamedCopy = await client.PutAsJsonAsync($"{path}/courses/{duplicates[0]}", metadata with { name = "Independent" });
        Assert.Equal(HttpStatusCode.OK, renamedCopy.StatusCode);

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var evt = await db.Events.SingleAsync(x => x.Id == eventId);
        var startAt = evt.StartDate.AddMinutes(10).ToString("yyyy-MM-ddTHH:mm:ss") + "Z";
        var first = await CreatedId(await client.PostAsJsonAsync(path + "/races", new { name = "First", courseId, startAt }));
        var second = await CreatedId(await client.PostAsJsonAsync(path + "/races", new { name = "Second", courseId, startAt }));
        await CreatedId(await client.PostAsJsonAsync(path + "/races", new { name = "Independent race", courseId = duplicates[0], startAt }));
        using var independentSwitch = await client.PutAsJsonAsync($"{path}/races/{first}", new { name = "First", courseId = duplicates[0], startAt });
        Assert.Equal(HttpStatusCode.BadRequest, independentSwitch.StatusCode);

        Assert.False(Course(await Configuration(), courseId).GetProperty("immutable").GetBoolean());
        using var edited = await client.PutAsJsonAsync(coursePath, metadata with { name = "Summit trail" });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        using var tooShort = await client.PutAsJsonAsync(coursePath, metadata with { name = "Summit trail", distanceKm = 4 });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        var ids = original.GetProperty("checkpoints").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray();
        var updates = points.Select((p, index) => p with { id = ids[index] }).ToArray();
        updates[1] = updates[1] with { sequence = 3, name = "Water stop" };
        updates[2] = updates[2] with { sequence = 2 };
        updates[3] = updates[3] with { distanceFromStartKm = 12 };
        using var changed = await client.PutAsJsonAsync(coursePath + "/checkpoints", new { distanceKm = 12, checkpoints = updates });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        var saved = await Configuration();
        Assert.Equal(12m, Course(saved, courseId).GetProperty("distanceKm").GetDecimal());
        Assert.Equal(new[] { ids[0], ids[2], ids[1], ids[3] },
            Course(saved, courseId).GetProperty("checkpoints").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
        foreach (var race in saved.GetProperty("races").EnumerateArray())
        {
            var id = race.GetProperty("id").GetGuid();
            Assert.Equal(id == first || id == second ? 12m : 10m, race.GetProperty("distanceKm").GetDecimal());
        }

        updates[2] = updates[2] with { code = "Á" };
        updates[3] = updates[3] with { distanceFromStartKm = 13 };
        using var rollback = await client.PutAsJsonAsync(coursePath + "/checkpoints", new { distanceKm = 13, checkpoints = updates });
        Assert.Equal(HttpStatusCode.Conflict, rollback.StatusCode);
        Assert.Equal(saved.GetRawText(), (await Configuration()).GetRawText());

        db.Registrations.Add(new Registration
        {
            EventId = eventId, OrganizerId = organizerId, RaceId = second, BibNumber = "123",
            Runner = new Runner { OrganizerId = organizerId, FirstName = "Synthetic", LastName = "Runner" }
        });
        await db.SaveChangesAsync();
        Assert.Equal("registrations", Course(await Configuration(), courseId).GetProperty("editBlock").GetString());
        using var lockedMetadata = await client.PutAsJsonAsync(coursePath, metadata with { name = "Locked" });
        using var lockedRoute = await client.PutAsJsonAsync(coursePath + "/checkpoints", new { distanceKm = 13, checkpoints = updates });
        Assert.Equal(HttpStatusCode.Conflict, lockedMetadata.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, lockedRoute.StatusCode);
        await CreatedId(await client.PostAsync(duplicatePath, null));

        evt.Status = EventStatus.RegistrationClosed;
        await db.SaveChangesAsync();
        using var ready = await client.PatchAsJsonAsync(path + "/status", new { status = "Ready" });
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var readyEdit = await client.PutAsJsonAsync($"{path}/courses/{duplicates[0]}", metadata);
        using var readyDuplicate = await client.PostAsync(duplicatePath, null);
        Assert.Equal(HttpStatusCode.Conflict, readyEdit.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, readyDuplicate.StatusCode);
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task HistoricalCourseRetirementPreservesReferencesAndRequiresNoActiveRace()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizerId);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));
        var path = $"/api/v1/events/{eventId}";
        var courseInput = new { name = "Trail", description = "Original course", distanceKm = 10 };
        var courseId = await CreatedId(await client.PostAsJsonAsync(path + "/courses", courseInput));
        var coursePath = $"{path}/courses/{courseId}";
        var statusPath = coursePath + "/status";
        var routePath = coursePath + "/checkpoints";
        var routeInput = new
        {
            distanceKm = 10,
            checkpoints = new[]
            {
                new RoutePoint(null, "Start", "S", 1, 0, IsStart: true),
                new RoutePoint(null, "Finish", "F", 2, 10, IsFinish: true)
            }
        };
        using var route = await client.PutAsJsonAsync(routePath, routeInput);
        Assert.Equal(HttpStatusCode.NoContent, route.StatusCode);
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var item = await db.Events.SingleAsync(x => x.Id == eventId);
        var startAt = item.StartDate.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ss") + "Z";
        var firstRace = new { courseId, name = "Historical race", startAt };
        var raceId = await CreatedId(await client.PostAsJsonAsync(path + "/races", firstRace));
        var racePath = $"{path}/races/{raceId}";
        var successorId = await CreatedId(await client.PostAsync(coursePath + "/revisions", null));
        var registration = new Registration
        {
            EventId = eventId, OrganizerId = organizerId, RaceId = raceId, BibNumber = "10",
            Runner = new Runner { OrganizerId = organizerId, FirstName = "Synthetic", LastName = "Runner" }
        };
        db.Registrations.Add(registration);
        await db.SaveChangesAsync();

        async Task<JsonElement> ReadCourse(Guid id) =>
            (await client.GetFromJsonAsync<JsonElement>(path + "/configuration")).GetProperty("courses")
            .EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
        var inactive = new { status = "Inactive" };
        var active = new { status = "Active" };
        var initial = await ReadCourse(courseId);
        Assert.True(initial.GetProperty("hasActiveRaces").GetBoolean());
        using var blocked = await client.PatchAsJsonAsync(statusPath, inactive);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("Active", (await ReadCourse(courseId)).GetProperty("status").GetString());

        using var retireRace = await client.DeleteAsync(racePath);
        Assert.Equal(HttpStatusCode.NoContent, retireRace.StatusCode);
        using var retired = await client.PatchAsJsonAsync(statusPath, inactive);
        Assert.Equal(HttpStatusCode.NoContent, retired.StatusCode);
        var historical = await ReadCourse(courseId);
        Assert.Equal("Inactive", historical.GetProperty("status").GetString());
        Assert.True(historical.GetProperty("immutable").GetBoolean());
        Assert.False(historical.GetProperty("usable").GetBoolean());
        Assert.False(historical.GetProperty("hasActiveRaces").GetBoolean());
        Assert.Equal(initial.GetProperty("checkpoints").GetRawText(), historical.GetProperty("checkpoints").GetRawText());
        Assert.Equal(initial.GetProperty("version").GetInt32(), historical.GetProperty("version").GetInt32());
        Assert.Equal(courseId, (await client.GetFromJsonAsync<JsonElement>(racePath)).GetProperty("courseId").GetGuid());
        Assert.Equal(raceId, await db.Registrations.Where(x => x.Id == registration.Id).Select(x => x.RaceId).SingleAsync());

        using var unavailable = await client.PostAsJsonAsync(path + "/races", firstRace with { name = "Unavailable race" });
        using var lockedMetadata = await client.PutAsJsonAsync(coursePath, courseInput);
        using var lockedRoute = await client.PutAsJsonAsync(routePath, routeInput);
        using var deletion = await client.DeleteAsync(coursePath);
        Assert.Equal(HttpStatusCode.BadRequest, unavailable.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, lockedMetadata.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, lockedRoute.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, deletion.StatusCode);

        using var reactivated = await client.PatchAsJsonAsync(statusPath, active);
        Assert.Equal(HttpStatusCode.NoContent, reactivated.StatusCode);
        var restored = await ReadCourse(courseId);
        Assert.True(restored.GetProperty("usable").GetBoolean());
        Assert.True(restored.GetProperty("immutable").GetBoolean());
        using var stillReadOnly = await client.PutAsJsonAsync(routePath, routeInput);
        using var metadataStillReadOnly = await client.PutAsJsonAsync(coursePath, courseInput);
        Assert.Equal(HttpStatusCode.Conflict, stillReadOnly.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, metadataStillReadOnly.StatusCode);

        var raceToSwitch = firstRace with { name = "Future race" };
        var switchId = await CreatedId(await client.PostAsJsonAsync(path + "/races", raceToSwitch));
        var switchPath = $"{path}/races/{switchId}";
        using var retireSuccessor = await client.PatchAsJsonAsync($"{path}/courses/{successorId}/status", inactive);
        Assert.Equal(HttpStatusCode.NoContent, retireSuccessor.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>(switchPath)).GetProperty("revisionIds").EnumerateArray());
        using var unavailableSwitch = await client.PutAsJsonAsync(switchPath, raceToSwitch with { courseId = successorId });
        Assert.Equal(HttpStatusCode.BadRequest, unavailableSwitch.StatusCode);

        item.Status = EventStatus.Ready;
        await db.SaveChangesAsync();
        using var lifecycleLocked = await client.PatchAsJsonAsync($"{path}/courses/{successorId}/status", active);
        Assert.Equal(HttpStatusCode.Conflict, lifecycleLocked.StatusCode);
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task RetiredStandaloneCoursesRequireReactivationBeforeEditing()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizerId);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var start = await db.Events.Where(x => x.Id == eventId).Select(x => x.StartDate).SingleAsync();
        var startAt = start.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ss") + "Z";
        var path = $"/api/v1/events/{eventId}";
        var points = new[]
        {
            new RoutePoint(null, "Start", "S", 1, 0, IsStart: true),
            new RoutePoint(null, "Finish", "F", 2, 10, IsFinish: true)
        };

        foreach (var scenario in new[] { (Name: "Unused course", HasRace: false), (Name: "Inactive race course", HasRace: true) })
        {
            var courseId = await CreatedId(await client.PostAsJsonAsync(path + "/courses", new { name = scenario.Name, distanceKm = 10 }));
            var coursePath = $"{path}/courses/{courseId}";
            var routePath = coursePath + "/checkpoints";
            using var initialRoute = await client.PutAsJsonAsync(routePath, new { distanceKm = 10, checkpoints = points });
            Assert.Equal(HttpStatusCode.NoContent, initialRoute.StatusCode);
            if (scenario.HasRace)
            {
                var raceId = await CreatedId(await client.PostAsJsonAsync(path + "/races", new { name = "Inactive race", courseId, startAt }));
                using var retireRace = await client.DeleteAsync($"{path}/races/{raceId}");
                Assert.Equal(HttpStatusCode.NoContent, retireRace.StatusCode);
            }

            async Task<JsonElement> ReadCourse() =>
                (await client.GetFromJsonAsync<JsonElement>(path + "/configuration")).GetProperty("courses")
                .EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == courseId);

            using var retired = await client.PatchAsJsonAsync(coursePath + "/status", new { status = "Inactive" });
            Assert.Equal(HttpStatusCode.NoContent, retired.StatusCode);
            var before = await ReadCourse();
            Assert.True(before.GetProperty("immutable").GetBoolean());
            Assert.Equal("inactive", before.GetProperty("editBlock").GetString());

            var ids = before.GetProperty("checkpoints").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray();
            var edits = points.Select((p, index) => p with { Id = ids[index] }).ToArray();
            edits[1] = edits[1] with { Name = "Finish gate" };
            var metadata = new { name = scenario.Name + " updated", distanceKm = 10 };
            var changedRoute = new { distanceKm = 10, checkpoints = edits };
            using var blockedMetadata = await client.PutAsJsonAsync(coursePath, metadata);
            using var blockedRoute = await client.PutAsJsonAsync(routePath, changedRoute);
            foreach (var response in new[] { blockedMetadata, blockedRoute })
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Equal("Reactivate this course before editing it.",
                    (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
            }
            Assert.Equal(before.GetRawText(), (await ReadCourse()).GetRawText());

            using var reactivated = await client.PatchAsJsonAsync(coursePath + "/status", new { status = "Active" });
            Assert.Equal(HttpStatusCode.NoContent, reactivated.StatusCode);
            Assert.False((await ReadCourse()).GetProperty("immutable").GetBoolean());
            using var editedMetadata = await client.PutAsJsonAsync(coursePath, metadata);
            using var editedRoute = await client.PutAsJsonAsync(routePath, changedRoute);
            Assert.Equal(HttpStatusCode.OK, editedMetadata.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, editedRoute.StatusCode);
            var after = await ReadCourse();
            Assert.Equal(metadata.name, after.GetProperty("name").GetString());
            Assert.Equal("Finish gate", after.GetProperty("checkpoints")[1].GetProperty("name").GetString());
            Assert.Equal(ids[1], after.GetProperty("checkpoints")[1].GetProperty("id").GetGuid());
        }
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task DirectDistanceEditUpdatesOnlyFinishAndAllReferencingRaces()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizerId);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));
        var path = $"/api/v1/events/{eventId}";
        var courseInput = new { name = "Distance Course", distanceKm = 10, description = "Original" };
        var courseId = await CreatedId(await client.PostAsJsonAsync(path + "/courses", courseInput));
        var coursePath = $"{path}/courses/{courseId}";
        var points = new[]
        {
            new { name = "Start", code = "S", sequence = 1, distanceFromStartKm = 0, isStart = true, isFinish = false,
                elevationMeters = 120, isMandatory = true, description = "Check in" },
            new { name = "Aid", code = "A", sequence = 2, distanceFromStartKm = 6, isStart = false, isFinish = false,
                elevationMeters = 500, isMandatory = false, description = "Water" },
            new { name = "Finish", code = "F", sequence = 3, distanceFromStartKm = 10, isStart = false, isFinish = true,
                elevationMeters = 140, isMandatory = true, description = "Finish gate" }
        };
        using var checkpoints = await client.PutAsJsonAsync(coursePath + "/checkpoints", new { distanceKm = 10, checkpoints = points });
        Assert.Equal(HttpStatusCode.NoContent, checkpoints.StatusCode);

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var evt = await db.Events.SingleAsync(x => x.Id == eventId);
        var startAt = evt.StartDate.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ss") + "Z";
        var first = await CreatedId(await client.PostAsJsonAsync(path + "/races", new { name = "First", courseId, startAt }));
        var second = await CreatedId(await client.PostAsJsonAsync(path + "/races", new { name = "Second", courseId, startAt }));
        using var inactiveRace = await client.DeleteAsync($"{path}/races/{second}");
        Assert.Equal(HttpStatusCode.NoContent, inactiveRace.StatusCode);

        async Task<JsonElement> ReadConfiguration() => await client.GetFromJsonAsync<JsonElement>(path + "/configuration");
        var initial = (await ReadConfiguration()).GetProperty("courses")[0].GetProperty("checkpoints");
        foreach (var distance in new[] { 12, 8 })
        {
            using var updated = await client.PutAsJsonAsync(coursePath, courseInput with { distanceKm = distance });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var config = await ReadConfiguration();
            var course = config.GetProperty("courses")[0];
            var savedPoints = course.GetProperty("checkpoints");
            Assert.Equal((decimal)distance, course.GetProperty("distanceKm").GetDecimal());
            Assert.Equal((decimal)distance, savedPoints[2].GetProperty("distanceFromStartKm").GetDecimal());
            for (var i = 0; i < initial.GetArrayLength(); i++)
            {
                foreach (var field in initial[i].EnumerateObject())
                {
                    if (i == 2 && field.Name == "distanceFromStartKm") continue;
                    Assert.Equal(field.Value.GetRawText(), savedPoints[i].GetProperty(field.Name).GetRawText());
                }
            }
            var races = config.GetProperty("races");
            Assert.Equal(2, races.GetArrayLength());
            Assert.All(races.EnumerateArray(), race => Assert.Equal((decimal)distance, race.GetProperty("distanceKm").GetDecimal()));
        }

        var before = await ReadConfiguration();
        using var invalid = await client.PutAsJsonAsync(coursePath, courseInput with { distanceKm = 5, description = "Must roll back" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("intermediate checkpoint", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal(before.GetRawText(), (await ReadConfiguration()).GetRawText());

        db.Registrations.Add(new Registration
        {
            EventId = eventId, OrganizerId = organizerId, RaceId = second, BibNumber = "123",
            RegistrationStatus = RegistrationStatus.Cancelled,
            Runner = new Runner { OrganizerId = organizerId, FirstName = "Synthetic", LastName = "Runner" }
        });
        await db.SaveChangesAsync();
        var locked = await ReadConfiguration();
        using var withCancelledRegistration = await client.PutAsJsonAsync(coursePath, courseInput with { distanceKm = 11 });
        Assert.Equal(HttpStatusCode.Conflict, withCancelledRegistration.StatusCode);
        Assert.Equal(locked.GetRawText(), (await ReadConfiguration()).GetRawText());

        evt.Status = EventStatus.RegistrationClosed;
        await db.SaveChangesAsync();
        using var ready = await client.PatchAsJsonAsync(path + "/status", new { status = "Ready" });
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(first, (await client.GetFromJsonAsync<JsonElement>($"{path}/races/{first}")).GetProperty("id").GetGuid());
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task PermanentCourseDeletionPreservesReferencedCoursesAndRevisionFamilies()
    {
        await using var data = new MySqlTestData(Services);
        var director = await data.CreateAccountAsync();
        var official = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(director, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizerId);
        var siblingEvent = await data.CreateEventAsync(organizerId);
        var foreignEvent = await data.CreateEventAsync(await data.CreateOrganizerAsync());
        using var client = Factory.CreateClient();
        using var readOnly = Factory.CreateClient();
        using var anonymous = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, director));
        readOnly.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(readOnly, official));

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        db.OrganizerMemberships.Add(new OrganizerMembership { OrganizerId = organizerId, UserId = official.Id, Role = OrganizerRole.TimingOfficial });
        await db.SaveChangesAsync();
        var item = await db.Events.SingleAsync(x => x.Id == eventId);
        var startAt = item.StartDate.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ss") + "Z";
        var path = $"/api/v1/events/{eventId}";
        var courses = path + "/courses";
        var points = new[]
        {
            new RoutePoint(null, "Start", "S", 1, 0, IsStart: true),
            new RoutePoint(null, "Finish", "F", 2, 10, IsFinish: true)
        };
        async Task<Guid> AddCourse(string name)
        {
            var id = await CreatedId(await client.PostAsJsonAsync(courses, new { name, distanceKm = 10 }));
            using var route = await client.PutAsJsonAsync($"{courses}/{id}/checkpoints", new { distanceKm = 10, checkpoints = points });
            Assert.Equal(HttpStatusCode.NoContent, route.StatusCode);
            return id;
        }
        async Task<JsonElement> ReadCourse(Guid id) =>
            (await client.GetFromJsonAsync<JsonElement>(path + "/configuration")).GetProperty("courses")
            .EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);

        var unused = await AddCourse("Unused");
        var unusedPath = $"{courses}/{unused}";
        using var unauthorized = await anonymous.DeleteAsync(unusedPath);
        using var forbidden = await readOnly.DeleteAsync(unusedPath);
        using var otherEvent = await client.DeleteAsync($"/api/v1/events/{siblingEvent}/courses/{unused}");
        using var otherOrganizer = await client.DeleteAsync($"/api/v1/events/{foreignEvent}/courses/{unused}");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherEvent.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherOrganizer.StatusCode);
        Assert.True((await ReadCourse(unused)).GetProperty("canDelete").GetBoolean());
        Assert.Equal(2, await db.CourseCheckpoints.CountAsync(x => x.CourseId == unused));
        using var deleted = await client.DeleteAsync(unusedPath);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(await db.Courses.AnyAsync(x => x.Id == unused));
        Assert.False(await db.CourseCheckpoints.AnyAsync(x => x.CourseId == unused));

        var referenced = await AddCourse("Referenced");
        var raceId = await CreatedId(await client.PostAsJsonAsync(path + "/races", new { courseId = referenced, name = "Race", startAt }));
        var racePath = $"{path}/races/{raceId}";
        using var activeConflict = await client.DeleteAsync($"{courses}/{referenced}");
        Assert.Equal(HttpStatusCode.Conflict, activeConflict.StatusCode);
        using var retired = await client.DeleteAsync(racePath);
        Assert.Equal(HttpStatusCode.NoContent, retired.StatusCode);
        var registration = new Registration
        {
            EventId = eventId, OrganizerId = organizerId, RaceId = raceId, BibNumber = "16",
            Runner = new Runner { OrganizerId = organizerId, FirstName = "Synthetic", LastName = "Runner" }
        };
        db.Registrations.Add(registration);
        await db.SaveChangesAsync();
        using var inactiveConflict = await client.DeleteAsync($"{courses}/{referenced}");
        Assert.Equal(HttpStatusCode.Conflict, inactiveConflict.StatusCode);
        Assert.False((await ReadCourse(referenced)).GetProperty("canDelete").GetBoolean());
        var fk = await Assert.ThrowsAsync<MySqlConnector.MySqlException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Courses WHERE Id = {referenced}"));
        Assert.Equal(1451, fk.Number);
        Assert.Equal(referenced, await db.Races.Where(x => x.Id == raceId).Select(x => x.CourseId).SingleAsync());
        Assert.True(await db.Registrations.AnyAsync(x => x.Id == registration.Id && x.RaceId == raceId));
        Assert.Equal(2, await db.CourseCheckpoints.CountAsync(x => x.CourseId == referenced));

        var firstRevision = await AddCourse("Historical");
        var latestRevision = await CreatedId(await client.PostAsync($"{courses}/{firstRevision}/revisions", null));
        foreach (var courseId in new[] { firstRevision, latestRevision })
        {
            Assert.False((await ReadCourse(courseId)).GetProperty("canDelete").GetBoolean());
            using var protectedRevision = await client.DeleteAsync($"{courses}/{courseId}");
            Assert.Equal(HttpStatusCode.Conflict, protectedRevision.StatusCode);
            Assert.Equal(2, await db.CourseCheckpoints.CountAsync(x => x.CourseId == courseId));
        }

        var locked = await AddCourse("Locked");
        item.Status = EventStatus.Ready;
        await db.SaveChangesAsync();
        using var lockedConflict = await client.DeleteAsync($"{courses}/{locked}");
        Assert.Equal(HttpStatusCode.Conflict, lockedConflict.StatusCode);
        Assert.True(await db.Courses.AnyAsync(x => x.Id == locked));
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task CourseDeletionAndRaceAssignmentCannotBothSucceed()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account);
        var eventId = await data.CreateEventAsync(organizerId);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var start = await db.Events.Where(x => x.Id == eventId).Select(x => x.StartDate).SingleAsync();
        var startAt = start.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ss") + "Z";
        var path = $"/api/v1/events/{eventId}";
        var courseId = await CreatedId(await client.PostAsJsonAsync(path + "/courses", new { name = "Concurrent", distanceKm = 10 }));
        var coursePath = $"{path}/courses/{courseId}";
        using var route = await client.PutAsJsonAsync(coursePath + "/checkpoints", new
        {
            distanceKm = 10, checkpoints = new[]
            {
                new RoutePoint(null, "Start", "S", 1, 0, IsStart: true),
                new RoutePoint(null, "Finish", "F", 2, 10, IsFinish: true)
            }
        });
        Assert.Equal(HttpStatusCode.NoContent, route.StatusCode);

        var deleteTask = client.DeleteAsync(coursePath);
        var assignTask = client.PostAsJsonAsync(path + "/races", new { name = "Concurrent race", courseId, startAt });
        using var deleted = await deleteTask;
        using var assigned = await assignTask;
        if (deleted.StatusCode == HttpStatusCode.NoContent)
        {
            Assert.Equal(HttpStatusCode.NotFound, assigned.StatusCode);
            Assert.False(await db.Courses.AnyAsync(x => x.Id == courseId));
            Assert.False(await db.Races.AnyAsync(x => x.EventId == eventId));
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
            Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
            Assert.True(await db.Courses.AnyAsync(x => x.Id == courseId));
            Assert.True(await db.Races.AnyAsync(x => x.CourseId == courseId));
        }
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
