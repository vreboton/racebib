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
            // The public projection also falls back for legacy whitespace stored outside the API.
            (await db.Registrations.SingleAsync(x => x.EventId == secondEventId)).BibDisplayName = "\t\u00A0";
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

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task CsvRequiresExplicitResolutionForHomonymsAndEmailConflicts()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizerId);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));
        await SeedRegistrationRace(client, eventId);
        var profiles = $"/api/v1/organizers/{organizerId}/runners";
        var path = $"/api/v1/events/{eventId}/registrations";
        var withoutEmail = await CreatedId(await client.PostAsJsonAsync(profiles, new { firstName = "Lin", lastName = "Santos" }));

        var missing = await PreviewRegistrationCsv(client, path, "First Name,Last Name,Race\nLin,Santos,10");
        Assert.Contains("Runner ID", missing.GetProperty("errorCsv").GetString());
        var unrelatedEmail = await PreviewRegistrationCsv(client, path,
            "First Name,Last Name,Email,Race\nLin,Santos,unmatched@example.test,10");
        Assert.Contains("email is missing or does not match", unrelatedEmail.GetProperty("errorCsv").GetString());

        var withEmail = await CreatedId(await client.PostAsJsonAsync(profiles,
            new { firstName = "Lin", lastName = "Santos", email = "lin-b@example.test" }));
        var conflictingEmail = await PreviewRegistrationCsv(client, path,
            "First Name,Last Name,Email,Race\nLian,Santos,lin-b@example.test,10");
        Assert.Contains("differently named profile", conflictingEmail.GetProperty("errorCsv").GetString());
        var conflictingId = await PreviewRegistrationCsv(client, path,
            $"First Name,Last Name,Email,Runner ID,Race\nLin,Santos,lin-b@example.test,{withoutEmail},10");
        Assert.Contains("conflicts with the selected Runner ID", conflictingId.GetProperty("errorCsv").GetString());

        const string header = "First Name,Last Name,Email,Runner ID,Race\n";
        var explicitRow = $"Lin,Santos,,{withoutEmail},10";
        var verifiedRow = "Lin,Santos,lin-b@example.test,,10";
        var repeated = await PreviewRegistrationCsv(client, path, header + verifiedRow + $"\nLin,Santos,,{withEmail},10");
        Assert.Contains("Duplicate registration", repeated.GetProperty("errorCsv").GetString());
        var file = header + explicitRow + "\n" + verifiedRow;
        var preview = await PreviewRegistrationCsv(client, path, file);
        Assert.True(preview.GetProperty("valid").GetBoolean());
        using var form = ImportForm(file, token: preview.GetProperty("token").GetString());
        using var imported = await client.PostAsync(path + "/import/confirm", form);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var registrations = (await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("items");
        Assert.Equal(new[] { withoutEmail, withEmail }.Order(),
            registrations.EnumerateArray().Select(x => x.GetProperty("runnerId").GetGuid()).Order());
        var reimport = await PreviewRegistrationCsv(client, path, file);
        Assert.Contains("Duplicate registration", reimport.GetProperty("errorCsv").GetString());
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task CsvCategoryEligibilityUsesVerifiedProfileBirthdates()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizerId);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));
        var raceId = await SeedRegistrationRace(client, eventId);
        var eventPath = $"/api/v1/events/{eventId}";
        await CreatedId(await client.PostAsJsonAsync($"{eventPath}/races/{raceId}/categories",
            new { name = "Masters", minimumAge = 40 }));
        var path = eventPath + "/registrations";
        var profiles = $"/api/v1/organizers/{organizerId}/runners";
        var runnerId = await CreatedId(await client.PostAsJsonAsync(profiles,
            new { firstName = "Alex", lastName = "Cruz", email = "alex@example.test", gender = "Original" }));
        await CreatedId(await client.PostAsJsonAsync(profiles,
            new { firstName = "Sam", lastName = "Cruz", email = "sam@example.test", birthdate = "2008-01-03" }));

        const string header = "First Name,Last Name,Email,Birthdate,Gender,Race Category,Age Group\n";
        const string withoutBirthdate = header + "Alex,Cruz,alex@example.test,,CSV value,10,Masters";
        var missing = await PreviewRegistrationCsv(client, path, withoutBirthdate);
        Assert.Contains("requires a birthdate on the runner profile", missing.GetProperty("errorCsv").GetString());
        var backfill = await PreviewRegistrationCsv(client, path,
            header + "Alex,Cruz,alex@example.test,1970-01-03,,10,Masters");
        Assert.Contains("save the birthdate on the profile", backfill.GetProperty("errorCsv").GetString());
        var underage = await PreviewRegistrationCsv(client, path, header + "Sam,Cruz,sam@example.test,,,10,Masters");
        Assert.Contains("outside the Age Group limits", underage.GetProperty("errorCsv").GetString());

        async Task SetBirthdate(string date)
        {
            using var updated = await client.PutAsJsonAsync($"{profiles}/{runnerId}", new
            {
                firstName = "Alex", lastName = "Cruz", email = "alex@example.test", birthdate = date, gender = "Original"
            });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        }

        await SetBirthdate("1970-01-03");
        var conflict = await PreviewRegistrationCsv(client, path, header + "Alex,Cruz,alex@example.test,1975-01-03,,10,Masters");
        Assert.Contains("CSV birthdate differs", conflict.GetProperty("errorCsv").GetString());
        var preview = await PreviewRegistrationCsv(client, path, withoutBirthdate);
        Assert.True(preview.GetProperty("valid").GetBoolean());
        Assert.Equal("Masters", preview.GetProperty("rows")[0].GetProperty("ageGroup").GetString());
        await SetBirthdate("1971-01-03");
        using var stale = ImportForm(withoutBirthdate, token: preview.GetProperty("token").GetString());
        using var rejected = await client.PostAsync(path + "/import/confirm", stale);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        preview = await PreviewRegistrationCsv(client, path, withoutBirthdate);
        using var form = ImportForm(withoutBirthdate, token: preview.GetProperty("token").GetString());
        using var imported = await client.PostAsync(path + "/import/confirm", form);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var stored = await client.GetFromJsonAsync<JsonElement>($"{profiles}/{runnerId}");
        Assert.Equal("1971-01-03", stored.GetProperty("birthdate").GetString());
        Assert.Equal("Original", stored.GetProperty("gender").GetString());
        var newRunner = await PreviewRegistrationCsv(client, path, header + "New,Runner,,1970-04-02,,10,Masters");
        Assert.True(newRunner.GetProperty("valid").GetBoolean());
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task ConcurrentManualAndCsvRegistrationsKeepOneEventRunnerPair()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizerId);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));
        var raceId = await SeedRegistrationRace(client, eventId);
        var profiles = $"/api/v1/organizers/{organizerId}/runners";
        var path = $"/api/v1/events/{eventId}/registrations";
        var runnerId = await CreatedId(await client.PostAsJsonAsync(profiles, new { firstName = "Dee", lastName = "Reyes" }));
        var file = $"First Name,Last Name,Runner ID,Race\nDee,Reyes,{runnerId},10";
        var previewA = await PreviewRegistrationCsv(client, path, file);
        var previewB = await PreviewRegistrationCsv(client, path, file);
        using var formA = ImportForm(file, token: previewA.GetProperty("token").GetString());
        using var formB = ImportForm(file, token: previewB.GetProperty("token").GetString());
        var responses = await Task.WhenAll(client.PostAsJsonAsync(path, new { raceId, runnerId }),
            client.PostAsJsonAsync(path, new { raceId, runnerId }),
            client.PostAsync(path + "/import/confirm", formA), client.PostAsync(path + "/import/confirm", formB));
        try
        {
            Assert.Single(responses, x => x.IsSuccessStatusCode);
            Assert.Equal(3, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        Assert.Equal(1, await db.Registrations.CountAsync(x => x.EventId == eventId && x.RunnerId == runnerId));
        db.Registrations.Add(new RaceTiming.Domain.Entities.Registration
        {
            EventId = eventId, OrganizerId = organizerId, RaceId = raceId, RunnerId = runnerId
        });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var duplicate = Assert.IsType<MySqlConnector.MySqlException>(error.InnerException);
        Assert.Equal(1062, duplicate.Number);
        Assert.Contains("UX_Registrations_Event_Runner_Slot", duplicate.Message);
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
