using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Persistence;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed partial class MySqlApiIntegrationTests
{
    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task RegistrationsEnforceScopeAndPreserveBibAndPublicIdentity()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var outsider = await data.CreateAccountAsync();
        await data.CreateOrganizerAsync(outsider);
        var organizerId = await data.CreateOrganizerAsync(account, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizerId);
        var secondEventId = await data.CreateEventAsync(organizerId);
        using var client = Factory.CreateClient();
        using var publicClient = Factory.CreateClient();
        using var outsideClient = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));
        outsideClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(outsideClient, outsider));
        var raceId = await SeedRegistrationRace(client, eventId);
        var secondRaceId = await SeedRegistrationRace(client, secondEventId);
        var profiles = $"/api/v1/organizers/{organizerId}/runners";
        var runnerId = await CreatedId(await client.PostAsJsonAsync(profiles, new
        {
            firstName = "María", lastName = "Dalisay", email = "fixture@example.test",
            birthdate = "1990-02-03", mobileNumber = "5550100", emergencyContact = "Private contact"
        }));
        var path = $"/api/v1/events/{eventId}/registrations";
        var input = new { runnerId, raceId, categoryId = (Guid?)null,
            bibDisplayName = "小光 🏃", bibNumber = "0007", status = "Confirmed", paymentStatus = "Paid" };
        using var denied = await outsideClient.PostAsJsonAsync(path, input);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var wrongRace = await client.PostAsJsonAsync(path, input with { raceId = secondRaceId });
        Assert.Equal(HttpStatusCode.BadRequest, wrongRace.StatusCode);
        using var wrongCategory = await client.PostAsJsonAsync(path, input with { categoryId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, wrongCategory.StatusCode);
        var id = await CreatedId(await client.PostAsJsonAsync(path, input));
        using var duplicate = await client.PostAsJsonAsync(path, input);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await CreatedId(await client.PostAsJsonAsync($"/api/v1/events/{secondEventId}/registrations",
            input with { raceId = secondRaceId, bibDisplayName = "  " }));

        var publicPath = $"/api/v1/public/events/{eventId}/participants";
        using var draft = await publicClient.GetAsync(publicPath);
        Assert.Equal(HttpStatusCode.NotFound, draft.StatusCode);
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
            foreach (var item in await db.Events.Where(x => x.Id == eventId || x.Id == secondEventId).ToListAsync())
                item.Status = EventStatus.Published;
            await db.SaveChangesAsync();
        }
        var participant = (await publicClient.GetFromJsonAsync<JsonElement>(publicPath))[0];
        Assert.Equal("小光 🏃", participant.GetProperty("nameOnBib").GetString());
        Assert.Equal("0007", participant.GetProperty("bibNumber").GetString());
        Assert.Equal(new[] { "bibNumber", "nameOnBib", "race" },
            participant.EnumerateObject().Select(x => x.Name).Order().ToArray());
        var fallback = (await publicClient.GetFromJsonAsync<JsonElement>(
            $"/api/v1/public/events/{secondEventId}/participants"))[0];
        Assert.Equal("María Dalisay", fallback.GetProperty("nameOnBib").GetString());

        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
            (await db.Registrations.SingleAsync(x => x.Id == id)).BibNumber = "Bíb";
            await db.SaveChangesAsync();
        }
        using var edit = await client.PutAsJsonAsync($"{path}/{id}", input with { bibDisplayName = "Maya", bibNumber = "Bíb" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        using var bibEdit = await client.PutAsJsonAsync($"{path}/{id}/bib", new { bibNumber = "0008" });
        Assert.Equal(HttpStatusCode.NoContent, bibEdit.StatusCode);
        using var cancel = await client.PostAsync($"{path}/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        var details = await client.GetFromJsonAsync<JsonElement>($"{path}/{id}");
        var registration = details.GetProperty("registration");
        Assert.Equal("Maya", registration.GetProperty("effectiveBibDisplayName").GetString());
        Assert.Equal("0008", registration.GetProperty("bibNumber").GetString());
        Assert.Equal(2, registration.GetProperty("paymentStatus").GetInt32());
        Assert.Equal(3, registration.GetProperty("status").GetInt32());
        Assert.Contains(details.GetProperty("history").EnumerateArray(),
            record => record.GetProperty("action").GetString() == "Registration.BibChanged");
        Assert.Empty((await publicClient.GetFromJsonAsync<JsonElement>(publicPath)).EnumerateArray());
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task CsvPreviewAndConfirmationValidateReplayStalenessAndConcurrentBibs()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var outsider = await data.CreateAccountAsync();
        await data.CreateOrganizerAsync(outsider);
        var organizerId = await data.CreateOrganizerAsync(account, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizerId);
        using var client = Factory.CreateClient();
        using var deniedClient = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));
        deniedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(deniedClient, outsider));
        var raceId = await SeedRegistrationRace(client, eventId);
        var path = $"/api/v1/events/{eventId}/registrations";
        const string mapping = """{"First Name":0,"Last Name":1,"Name on Bib":2,"Race":3,"Bib Number":4}""";
        const string mixed = """{"mode":2,"start":1,"end":9,"width":3}""";
        const string file = "\uFEFFGiven,Family,Preferred Name,Distance,Bib\r\nAna,Reyes,\"Ána, Junior\",10,\r\nBen,Cruz,Benny,10,001\r\n";
        using var deniedForm = ImportForm(file, mapping, mixed);
        using var denied = await deniedClient.PostAsync(path + "/import/preview", deniedForm);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var malformedForm = ImportForm("First Name,Last Name,Race\nAna,Re\"yes,10");
        using var malformed = await client.PostAsync(path + "/import/preview", malformedForm);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        var missing = await PreviewRegistrationCsv(client, path, "First Name,Race\nAna,10");
        Assert.False(missing.GetProperty("valid").GetBoolean());
        var duplicate = await PreviewRegistrationCsv(client, path, file + "Ana,Reyes,Ana,10,003\r\n", mapping, mixed);
        Assert.False(duplicate.GetProperty("valid").GetBoolean());
        Assert.Contains("Duplicate", duplicate.GetProperty("errorCsv").GetString());
        var fixture = await PreviewRegistrationCsv(client, path, file, mapping, mixed);
        Assert.True(fixture.GetProperty("valid").GetBoolean());
        var token = fixture.GetProperty("token").GetString();

        using var staleForm = ImportForm(file.Replace("Benny", "Ben"), mapping, mixed, token);
        using var stale = await client.PostAsync(path + "/import/confirm", staleForm);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("total").GetInt32());
        using var validForm = ImportForm(file, mapping, mixed, token);
        using var imported = await client.PostAsync(path + "/import/confirm", validForm);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        using var replayForm = ImportForm(file, mapping, mixed, token);
        using var replay = await client.PostAsync(path + "/import/confirm", replayForm);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var reimport = await PreviewRegistrationCsv(client, path, file.Replace("Ána, Junior", "Replacement"), mapping, mixed);
        Assert.False(reimport.GetProperty("valid").GetBoolean());
        var list = (await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("items");
        Assert.Equal("001", list[0].GetProperty("bibNumber").GetString());
        Assert.Equal("002", list[1].GetProperty("bibNumber").GetString());
        Assert.Equal("Ána, Junior", list[1].GetProperty("effectiveBibDisplayName").GetString());

        var runnerUrl = $"/api/v1/organizers/{organizerId}/runners";
        // A legacy bib can collide under MySQL's accent-insensitive collation.
        // An otherwise valid batch must roll back its new registrations and profiles.
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
            var legacy = await db.Registrations.SingleAsync(x => x.EventId == eventId && x.BibNumber == "001");
            legacy.BibNumber = "Á";
            await db.SaveChangesAsync();
        }
        const string collidingFile = "First Name,Last Name,Race,Bib Number\nTest,Alpha,10,UNIQUE\nTest,Zulu,10,A";
        var colliding = await PreviewRegistrationCsv(client, path, collidingFile);
        using var collisionForm = ImportForm(collidingFile, token: colliding.GetProperty("token").GetString());
        using var collision = await client.PostAsync(path + "/import/confirm", collisionForm);
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("total").GetInt32());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>(runnerUrl + "?search=Test"))
            .GetProperty("total").GetInt32());

        var first = await CreatedId(await client.PostAsJsonAsync(runnerUrl, new { firstName = "Solo", lastName = "One" }));
        var second = await CreatedId(await client.PostAsJsonAsync(runnerUrl, new { firstName = "Solo", lastName = "Two" }));
        var results = await Task.WhenAll(new[] { first, second }.Select(runnerId => client.PostAsJsonAsync(path,
            new { runnerId, raceId, assignment = new { mode = 1, start = 3, end = 3, width = 3 } })));
        using var result1 = results[0];
        using var result2 = results[1];
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.Conflict);
    }

    private async Task<Guid> SeedRegistrationRace(HttpClient client, Guid eventId)
    {
        var path = $"/api/v1/events/{eventId}";
        var courseId = await CreatedId(await client.PostAsJsonAsync(path + "/courses", new { name = "Course", distanceKm = 10 }));
        using var route = await client.PutAsJsonAsync($"{path}/courses/{courseId}/checkpoints", new
        {
            distanceKm = 10,
            checkpoints = new[]
            {
                new RoutePoint(null, "Start", "START", 1, 0, IsStart: true),
                new RoutePoint(null, "Finish", "FINISH", 2, 10, IsFinish: true)
            }
        });
        Assert.Equal(HttpStatusCode.NoContent, route.StatusCode);
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var startAt = await db.Events.Where(x => x.Id == eventId).Select(x => x.StartDate).SingleAsync();
        return await CreatedId(await client.PostAsJsonAsync(path + "/races",
            new { name = "10K", courseId, startAt = DateTime.SpecifyKind(startAt.AddMinutes(10), DateTimeKind.Utc) }));
    }

    private static async Task<JsonElement> PreviewRegistrationCsv(HttpClient client, string path, string file,
        string? mapping = null, string? assignment = null)
    {
        using var form = ImportForm(file, mapping, assignment);
        using var response = await client.PostAsync(path + "/import/preview", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static MultipartFormDataContent ImportForm(string file, string? mapping = null,
        string? assignment = null, string? token = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(file, Encoding.UTF8, "text/csv"), "file", "fixture.csv");
        if (mapping is not null) form.Add(new StringContent(mapping), "mapping");
        if (assignment is not null) form.Add(new StringContent(assignment), "assignment");
        if (token is not null) form.Add(new StringContent(token), "token");
        return form;
    }
}
