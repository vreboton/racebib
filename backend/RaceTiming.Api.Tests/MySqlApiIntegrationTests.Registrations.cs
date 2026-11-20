using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
        const string mixed = """{"mode":2,"start":1,"end":9,"width":3}""";
        const string file = "\uFEFFFirst Name,Last Name,Name on Bib,Email,Race,Bib Number\r\nÁna,Reyes,\"Ána, Junior\",ana@example.test,10K,\r\nBen,Cruz,Benny,ben@example.test,10K,001\r\n";
        using var deniedForm = ImportForm(raceId, file, mixed);
        using var denied = await deniedClient.PostAsync(path + "/import/preview", deniedForm);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var malformedForm = ImportForm(raceId, "First Name,Last Name,Email\nAna,Re\"yes,ana@example.test");
        using var malformed = await client.PostAsync(path + "/import/preview", malformedForm);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        var missing = await PreviewRegistrationCsv(client, path, raceId, "First Name,Race\nAna,10");
        Assert.False(missing.GetProperty("valid").GetBoolean());
        var duplicate = await PreviewRegistrationCsv(client, path, raceId, file + "Ána,Reyes,Ana,ana@example.test,10K,003\r\n", mixed);
        Assert.False(duplicate.GetProperty("valid").GetBoolean());
        Assert.Contains("Duplicate", duplicate.GetProperty("errorCsv").GetString());
        var fixture = await PreviewRegistrationCsv(client, path, raceId, file, mixed);
        Assert.True(fixture.GetProperty("valid").GetBoolean());
        var token = fixture.GetProperty("token").GetString();

        using var staleForm = ImportForm(raceId, file.Replace("Benny", "Ben"), mixed, token);
        using var stale = await client.PostAsync(path + "/import/confirm", staleForm);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("total").GetInt32());
        using var validForm = ImportForm(raceId, file, mixed, token);
        using var imported = await client.PostAsync(path + "/import/confirm", validForm);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        using var replayForm = ImportForm(raceId, file, mixed, token);
        using var replay = await client.PostAsync(path + "/import/confirm", replayForm);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var reimport = await PreviewRegistrationCsv(client, path, raceId, file.Replace("Ána, Junior", "Replacement"), mixed);
        Assert.False(reimport.GetProperty("valid").GetBoolean());
        var list = (await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("items");
        Assert.Equal("001", list[0].GetProperty("bibNumber").GetString());
        Assert.Equal("002", list[1].GetProperty("bibNumber").GetString());
        Assert.Equal("Ána, Junior", list[1].GetProperty("effectiveBibDisplayName").GetString());
        Assert.Equal("Ána", list[1].GetProperty("runner").GetProperty("firstName").GetString());

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
        const string collidingFile = "First Name,Last Name,Email,Race,Bib Number\nTest,Alpha,alpha@example.test,10K,UNIQUE\nTest,Zulu,zulu@example.test,10K,A";
        var colliding = await PreviewRegistrationCsv(client, path, raceId, collidingFile);
        using var collisionForm = ImportForm(raceId, collidingFile, token: colliding.GetProperty("token").GetString());
        using var collision = await client.PostAsync(path + "/import/confirm", collisionForm);
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("total").GetInt32());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>(runnerUrl + "?search=alpha%40example.test"))
            .GetProperty("total").GetInt32());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>(runnerUrl + "?search=zulu%40example.test"))
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
    public async Task CsvMatchesOrganizerEmailWithoutOverwritingProfiles()
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
        var runnerId = await CreatedId(await client.PostAsJsonAsync(profiles,
            new { firstName = "Lin", lastName = "Santos", email = "lin@example.test", gender = "Original" }));

        const string header = "First Name,Last Name,Email,Gender,Name on Bib\n";
        var bad = await PreviewRegistrationCsv(client, path, raceId,
            header + "Missing,Email,,,\nInvalid,Email,bad@host,,");
        Assert.Contains("Email is required", bad.GetProperty("errorCsv").GetString());
        Assert.Contains("Enter a valid email address", bad.GetProperty("errorCsv").GetString());
        var duplicate = await PreviewRegistrationCsv(client, path, raceId,
            header + "A,Runner, DUPLICATE@example.test ,,\nB,Runner,duplicate@example.test,,");
        Assert.Contains("Duplicate email", duplicate.GetProperty("errorCsv").GetString());

        const string file = header + " Changed , Name , LIN@EXAMPLE.TEST ,Changed,\nAnother,Runner, NEW@EXAMPLE.TEST ,New,   ";
        var preview = await PreviewRegistrationCsv(client, path, raceId, file);
        Assert.True(preview.GetProperty("valid").GetBoolean());
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>(profiles)).GetProperty("total").GetInt32());
        var rows = preview.GetProperty("rows");
        Assert.Equal("Changed Name", rows[0].GetProperty("nameOnBib").GetString());
        Assert.Equal("Another Runner", rows[1].GetProperty("nameOnBib").GetString());
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("error").ValueKind);
        Assert.Contains("CSV name Changed Name", rows[0].GetProperty("warning").GetString());
        Assert.Contains("saved profile for Lin Santos", rows[0].GetProperty("warning").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("warning").ValueKind);
        Assert.DoesNotContain("saved profile for Lin Santos", preview.GetProperty("errorCsv").GetString());
        using var form = ImportForm(raceId, file, token: preview.GetProperty("token").GetString());
        using var imported = await client.PostAsync(path + "/import/confirm", form);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);

        var stored = await client.GetFromJsonAsync<JsonElement>($"{profiles}/{runnerId}");
        Assert.Equal("Lin", stored.GetProperty("firstName").GetString());
        Assert.Equal("Santos", stored.GetProperty("lastName").GetString());
        Assert.Equal("Original", stored.GetProperty("gender").GetString());
        var profilesAfter = (await client.GetFromJsonAsync<JsonElement>(profiles)).GetProperty("items");
        Assert.Contains(profilesAfter.EnumerateArray(), row => row.GetProperty("email").GetString() == "new@example.test");
        var registrations = (await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, registrations.Length);
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var persisted = await db.Registrations.Where(row => row.EventId == eventId).ToListAsync();
        Assert.Equal("Changed Name", persisted.Single(row => row.RunnerId == runnerId).BibDisplayName);
        Assert.Equal("Another Runner", persisted.Single(row => row.RunnerId != runnerId).BibDisplayName);
        Assert.All(registrations, row =>
        {
            Assert.Equal((int)RegistrationStatus.Confirmed, row.GetProperty("status").GetInt32());
            Assert.Equal((int)PaymentStatus.Paid, row.GetProperty("paymentStatus").GetInt32());
        });
        var reimport = await PreviewRegistrationCsv(client, path, raceId, file);
        Assert.Contains("Duplicate registration", reimport.GetProperty("errorCsv").GetString());
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task CsvConfirmationRejectsStaleRunnerIdentityAndKeepsEquivalentEmailValid()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizerId);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));
        var raceId = await SeedRegistrationRace(client, eventId);
        var profilePath = $"/api/v1/organizers/{organizerId}/runners";
        var path = $"/api/v1/events/{eventId}/registrations";
        const string email = "lin@example.test";
        const string file = "First Name,Last Name,Email,Name on Bib\nLin,Santos,lin@example.test,L.S.";
        var runnerId = await CreatedId(await client.PostAsJsonAsync(profilePath,
            new { firstName = "Lin", lastName = "Santos", email }));

        async Task UpdateProfile(string firstName, string profileEmail)
        {
            using var response = await client.PutAsJsonAsync($"{profilePath}/{runnerId}",
                new { firstName, lastName = "Santos", email = profileEmail });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        async Task Confirm(JsonElement preview, HttpStatusCode expected)
        {
            Assert.True(preview.GetProperty("valid").GetBoolean());
            using var form = ImportForm(raceId, file, token: preview.GetProperty("token").GetString());
            using var response = await client.PostAsync(path + "/import/confirm", form);
            Assert.Equal(expected, response.StatusCode);
        }

        var original = await PreviewRegistrationCsv(client, path, raceId, file);
        Assert.Equal(JsonValueKind.Null, original.GetProperty("rows")[0].GetProperty("warning").ValueKind);
        await UpdateProfile("Lina", email);
        await Confirm(original, HttpStatusCode.Conflict);

        var renamed = await PreviewRegistrationCsv(client, path, raceId, file);
        var warned = renamed.GetProperty("rows")[0];
        Assert.Equal("L.S.", warned.GetProperty("nameOnBib").GetString());
        Assert.Contains("Lina Santos", warned.GetProperty("warning").GetString());
        await UpdateProfile("Lina", "new-address@example.test");
        await Confirm(renamed, HttpStatusCode.Conflict);
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>(profilePath)).GetProperty("total").GetInt32());

        // Reassign the original email to a different profile with the same other identity fields.
        var replacementId = await CreatedId(await client.PostAsJsonAsync(profilePath,
            new { firstName = "Lina", lastName = "Santos", email }));
        await Confirm(renamed, HttpStatusCode.Conflict);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("total").GetInt32());

        var replacementPreview = await PreviewRegistrationCsv(client, path, raceId, file);
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var replacement = await db.Runners.SingleAsync(x => x.Id == replacementId);
        replacement.Email = " LIN@EXAMPLE.TEST ";
        await db.SaveChangesAsync();

        await Confirm(replacementPreview, HttpStatusCode.OK);
        var saved = await db.Registrations.SingleAsync(x => x.EventId == eventId);
        Assert.Equal(replacementId, saved.RunnerId);
        Assert.Equal("L.S.", saved.BibDisplayName);
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

        const string header = "First Name,Last Name,Email,Birthdate,Gender,Race,Category\n";
        const string withoutBirthdate = header + "Alex,Cruz,alex@example.test,,CSV value,10K,Masters";
        const string differentBirthdate = header + "Alex,Cruz,alex@example.test,2008-01-03,CSV value,10K,Masters";
        var missing = await PreviewRegistrationCsv(client, path, raceId, withoutBirthdate);
        Assert.Contains("requires a birthdate on the runner profile", missing.GetProperty("errorCsv").GetString());
        var backfill = await PreviewRegistrationCsv(client, path, raceId,
            header + "Alex,Cruz,alex@example.test,1970-01-03,,10,Masters");
        Assert.Contains("requires a birthdate on the runner profile", backfill.GetProperty("errorCsv").GetString());
        var underage = await PreviewRegistrationCsv(client, path, raceId, header + "Sam,Cruz,sam@example.test,1970-01-03,,10K,Masters");
        Assert.Contains("outside the category age limits", underage.GetProperty("errorCsv").GetString());

        async Task SetBirthdate(string date)
        {
            using var updated = await client.PutAsJsonAsync($"{profiles}/{runnerId}", new
            {
                firstName = "Alex", lastName = "Cruz", email = "alex@example.test", birthdate = date, gender = "Original"
            });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        }

        await SetBirthdate("1970-01-03");
        var invalid = await PreviewRegistrationCsv(client, path, raceId,
            header + "Alex,Cruz,alex@example.test,not-a-date,,10K,Masters");
        Assert.Contains("Use a valid birthdate", invalid.GetProperty("errorCsv").GetString());
        var preview = await PreviewRegistrationCsv(client, path, raceId, withoutBirthdate);
        Assert.True(preview.GetProperty("valid").GetBoolean());
        await SetBirthdate("1971-01-03");
        using var stale = ImportForm(raceId, withoutBirthdate, token: preview.GetProperty("token").GetString());
        using var rejected = await client.PostAsync(path + "/import/confirm", stale);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        preview = await PreviewRegistrationCsv(client, path, raceId, differentBirthdate);
        Assert.True(preview.GetProperty("valid").GetBoolean());
        using var form = ImportForm(raceId, differentBirthdate, token: preview.GetProperty("token").GetString());
        using var imported = await client.PostAsync(path + "/import/confirm", form);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var stored = await client.GetFromJsonAsync<JsonElement>($"{profiles}/{runnerId}");
        Assert.Equal("1971-01-03", stored.GetProperty("birthdate").GetString());
        Assert.Equal("Original", stored.GetProperty("gender").GetString());
        const string newFile = header + "New,Runner,new@example.test,1970-04-02,,10K,Masters";
        var newRunner = await PreviewRegistrationCsv(client, path, raceId, newFile);
        Assert.True(newRunner.GetProperty("valid").GetBoolean());
        using var newForm = ImportForm(raceId, newFile, token: newRunner.GetProperty("token").GetString());
        using var newImport = await client.PostAsync(path + "/import/confirm", newForm);
        Assert.Equal(HttpStatusCode.OK, newImport.StatusCode);
        var created = (await client.GetFromJsonAsync<JsonElement>(profiles + "?search=new%40example.test")).GetProperty("items");
        Assert.Equal("1970-04-02", created[0].GetProperty("birthdate").GetString());
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
        var runnerId = await CreatedId(await client.PostAsJsonAsync(profiles, new { firstName = "Dee", lastName = "Reyes", email = "dee@example.test" }));
        var file = "First Name,Last Name,Email,Race\nDee,Reyes,dee@example.test,10K";
        var previewA = await PreviewRegistrationCsv(client, path, raceId, file);
        var previewB = await PreviewRegistrationCsv(client, path, raceId, file);
        using var formA = ImportForm(raceId, file, token: previewA.GetProperty("token").GetString());
        using var formB = ImportForm(raceId, file, token: previewB.GetProperty("token").GetString());
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

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task CsvTemplateUsesExactCanonicalHeadersAndSupportsReordering()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizerId);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));
        var raceId = await SeedRegistrationRace(client, eventId);
        var path = $"/api/v1/events/{eventId}/registrations";
        const string header = "First Name,Last Name,Name on Bib,Gender,Birthdate,Email,Mobile Number,Race,Category,Start Wave,Bib Number,Team,Emergency Contact";
        var file = "\uFEFF" + header + "\r\nMara,Ventura,小光,,,mara@example.test,,10K,,,0007,,\r\n";
        var standard = await PreviewRegistrationCsv(client, path, raceId, file);
        Assert.True(standard.GetProperty("valid").GetBoolean());
        Assert.Equal("0007", standard.GetProperty("rows")[0].GetProperty("bibNumber").GetString());
        Assert.Equal("小光", standard.GetProperty("rows")[0].GetProperty("nameOnBib").GetString());

        const string reorderedFile = "Bib Number,Email,Race,Last Name,First Name\n0008,mara@example.test,10K,Ventura,Mara";
        var reordered = await PreviewRegistrationCsv(client, path, raceId, reorderedFile);
        Assert.True(reordered.GetProperty("valid").GetBoolean());
        Assert.Equal("Mara Ventura", reordered.GetProperty("rows")[0].GetProperty("nameOnBib").GetString());
        using var form = ImportForm(raceId, reorderedFile, token: reordered.GetProperty("token").GetString());
        using var confirmed = await client.PostAsync(path + "/import/confirm", form);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        foreach (var (bad, message) in new[]
        {
            ("First Name,Last Name\nA,B", "Missing required headers: Email"),
            ("First Name,Last Name,Race,Race\nA,B,10,10", "Duplicate header 'Race'"),
            ("First Name,Last Name,Race,Preferred Name\nA,B,10,Alias", "Unrecognized header in column 4"),
            ("First Name,Last Name,race\nA,B,10", "Unrecognized header in column 3"),
            ("First Name,Last Name,Race \nA,B,10", "Unrecognized header in column 3")
        })
        {
            var preview = await PreviewRegistrationCsv(client, path, raceId, bad);
            Assert.False(preview.GetProperty("valid").GetBoolean());
            Assert.Equal(JsonValueKind.Null, preview.GetProperty("token").ValueKind);
            Assert.Contains(message, preview.GetProperty("error").GetString());
            Assert.Contains("RaceBib CSV template", preview.GetProperty("error").GetString());
        }

        var blanks = await PreviewRegistrationCsv(client, path, raceId, "First Name,Last Name,Email\n ,Runner,a@example.test\nSecond, ,b@example.test\nThird,Runner, ");
        Assert.False(blanks.GetProperty("valid").GetBoolean());
        Assert.Contains("First and last names are required", blanks.GetProperty("errorCsv").GetString());
        Assert.Contains("Email is required", blanks.GetProperty("errorCsv").GetString());
        using var malformedForm = ImportForm(raceId, "First Name,Last Name,Email\nAna,Re\"yes,ana@example.test");
        using var malformed = await client.PostAsync(path + "/import/preview", malformedForm);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Contains("well-formed UTF-8 CSV", await malformed.Content.ReadAsStringAsync());
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task CsvResolvesEventRaceNamesAndReportsDefaultRaceFallback()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizerId);
        var otherEventId = await data.CreateEventAsync(organizerId);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));
        var raceId = await SeedRegistrationRace(client, eventId);
        var otherRaceId = await SeedRegistrationRace(client, otherEventId);
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var race = await db.Races.SingleAsync(x => x.Id == raceId);
        var named = new Race { Name = "Summit", EventId = eventId, CourseId = race.CourseId, StartAtUtc = race.StartAtUtc, DistanceKm = 10 };
        var inactive = new Race { Name = "Retired", EventId = eventId, CourseId = race.CourseId, StartAtUtc = race.StartAtUtc, DistanceKm = 10, Status = RecordStatus.Inactive };
        var category = new RaceCategory { RaceId = named.Id, Name = "Open" };
        var wave = new StartWave { RaceId = named.Id, Name = "Early", Sequence = 1, StartAtUtc = race.StartAtUtc };
        db.AddRange(named, inactive, category, wave);
        await db.SaveChangesAsync();

        var path = $"/api/v1/events/{eventId}/registrations";
        const string header = "First Name,Last Name,Email,Race,Category,Start Wave\n";
        const string file = header + "A,Runner,a@example.test,Summit,Open,Early\nB,Runner,b@example.test, sUmMiT ,,Early\n"
            + "C,Runner,c@example.test,Summit,Open,\nD,Runner,d@example.test,,,\nE,Runner,e@example.test,Unknown,,";
        foreach (var invalid in new[] { Guid.Empty, Guid.NewGuid(), otherRaceId, inactive.Id })
        {
            var rejected = await PreviewRegistrationCsv(client, path, invalid, file);
            Assert.False(rejected.GetProperty("valid").GetBoolean());
            Assert.Contains("active default Race Category", rejected.GetProperty("error").GetString());
        }
        var invalidRow = await PreviewRegistrationCsv(client, path, raceId,
            header + "F,Runner,f@example.test,Unknown,Open,Early\nG,Runner,g@example.test,Retired,,");
        Assert.Contains("Age Group or Start Wave does not belong to this Race Category", invalidRow.GetProperty("errorCsv").GetString());
        Assert.Contains("CSV Race Category is inactive", invalidRow.GetProperty("errorCsv").GetString());

        var preview = await PreviewRegistrationCsv(client, path, raceId, file);
        Assert.True(preview.GetProperty("valid").GetBoolean());
        Assert.Equal(2, preview.GetProperty("defaultRaceCount").GetInt32());
        var rows = preview.GetProperty("rows");
        Assert.Equal("Summit", rows[1].GetProperty("raceName").GetString());
        Assert.Equal("blank", rows[3].GetProperty("defaultRaceReason").GetString());
        Assert.Equal("unrecognized", rows[4].GetProperty("defaultRaceReason").GetString());
        Assert.Equal("Unknown", rows[4].GetProperty("csvRace").GetString());
        var token = preview.GetProperty("token").GetString();
        using var changed = ImportForm(named.Id, file, token: token);
        using var changedResult = await client.PostAsync(path + "/import/confirm", changed);
        Assert.Equal(HttpStatusCode.Conflict, changedResult.StatusCode);
        using var form = ImportForm(raceId, file, token: token);
        using var accepted = await client.PostAsync(path + "/import/confirm", form);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(2, (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("defaultRaceCount").GetInt32());
        var saved = await db.Registrations.Where(x => x.EventId == eventId).Include(x => x.Runner)
            .ToDictionaryAsync(x => x.Runner.FirstName);
        Assert.Equal(named.Id, saved["A"].RaceId);
        Assert.Equal(category.Id, saved["A"].RaceCategoryId);
        Assert.Equal(wave.Id, saved["A"].StartWaveId);
        Assert.Null(saved["B"].RaceCategoryId);
        Assert.Null(saved["C"].StartWaveId);
        Assert.Equal(raceId, saved["D"].RaceId);
        Assert.Null(saved["D"].RaceCategoryId);
        Assert.Null(saved["D"].StartWaveId);
        Assert.Equal(raceId, saved["E"].RaceId);

        // A leading-space legacy spelling can be distinct in MySQL but ambiguous after trimming.
        db.Races.Add(new Race { Name = " Summit", EventId = eventId, CourseId = race.CourseId, StartAtUtc = race.StartAtUtc, DistanceKm = 10 });
        await db.SaveChangesAsync();
        var ambiguous = await PreviewRegistrationCsv(client, path, raceId, header + "F,Runner,f@example.test,Summit,,");
        Assert.Contains("matches multiple Race Categories", ambiguous.GetProperty("errorCsv").GetString());
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task ConcurrentCsvConfirmsInTwoEventsReuseOneOrganizerProfile()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account, OrganizerRole.RaceDirector);
        var firstEvent = await data.CreateEventAsync(organizerId);
        var secondEvent = await data.CreateEventAsync(organizerId);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));
        var firstRace = await SeedRegistrationRace(client, firstEvent);
        var secondRace = await SeedRegistrationRace(client, secondEvent);
        var paths = new[] { ($"/api/v1/events/{firstEvent}/registrations", firstRace), ($"/api/v1/events/{secondEvent}/registrations", secondRace) };
        const string file = "First Name,Last Name,Email\nPool,Runner,shared@example.test";
        var previews = await Task.WhenAll(paths.Select(pair => PreviewRegistrationCsv(client, pair.Item1, pair.Item2, file)));
        async Task<HttpStatusCode> Confirm(int index, JsonElement preview)
        {
            using var form = ImportForm(paths[index].Item2, file, token: preview.GetProperty("token").GetString());
            using var response = await client.PostAsync(paths[index].Item1 + "/import/confirm", form);
            return response.StatusCode;
        }
        var results = await Task.WhenAll(Confirm(0, previews[0]), Confirm(1, previews[1]));
        Assert.Single(results, status => status == HttpStatusCode.OK);
        Assert.Single(results, status => status == HttpStatusCode.Conflict);
        var staleIndex = Array.IndexOf(results, HttpStatusCode.Conflict);
        var stale = paths[staleIndex];
        var fresh = await PreviewRegistrationCsv(client, stale.Item1, stale.Item2, file);
        Assert.Equal(HttpStatusCode.OK, await Confirm(staleIndex, fresh));

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var runner = await db.Runners.SingleAsync(x => x.OrganizerId == organizerId && x.Email == "shared@example.test");
        Assert.Equal(2, await db.Registrations.CountAsync(x => x.RunnerId == runner.Id));
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

    private static async Task<JsonElement> PreviewRegistrationCsv(HttpClient client, string path, Guid raceId, string file,
        string? assignment = null)
    {
        using var form = ImportForm(raceId, file, assignment);
        using var response = await client.PostAsync(path + "/import/preview", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static MultipartFormDataContent ImportForm(Guid raceId, string file, string? assignment = null, string? token = null)
    {
        var form = new MultipartFormDataContent();
        if (raceId != Guid.Empty) form.Add(new StringContent(raceId.ToString()), "defaultRaceId");
        form.Add(new StringContent(file, Encoding.UTF8, "text/csv"), "file", "fixture.csv");
        if (assignment is not null) form.Add(new StringContent(assignment), "assignment");
        if (token is not null) form.Add(new StringContent(token), "token");
        return form;
    }
}
