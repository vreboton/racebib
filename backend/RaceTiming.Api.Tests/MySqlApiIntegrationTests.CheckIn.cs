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
    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task CheckInIsVersionedAuditedAndProtectsAssignmentsUntilReversed()
    {
        await using var data = new MySqlTestData(Services);
        var director = await data.CreateAccountAsync();
        var administrator = await data.CreateAccountAsync();
        var outsider = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(director, OrganizerRole.RaceDirector);
        await data.CreateOrganizerAsync(outsider);
        var eventId = await data.CreateEventAsync(organizerId);
        var otherEventId = await data.CreateEventAsync(organizerId);
        using var first = await CheckInClient(director);
        using var second = await CheckInClient(administrator);
        using var outside = await CheckInClient(outsider);
        using var anonymous = Factory.CreateClient();

        var raceId = await SeedRegistrationRace(first, eventId, ensureRegistrationCategory: true);
        var runnerId = await CreatedId(await first.PostAsJsonAsync($"/api/v1/organizers/{organizerId}/runners",
            new { firstName = "Lina", lastName = "Fixture", email = "private-check-in@example.test" }));
        var list = $"/api/v1/events/{eventId}/registrations";
        var input = new { raceId, runnerId, bibNumber = "0007", status = RegistrationStatus.Confirmed,
            paymentStatus = PaymentStatus.Unpaid, version = 1L, categoryId = (Guid?)null, waveId = (Guid?)null, team = (string?)null };
        var id = await CreatedId(await first.PostAsJsonAsync(list, input));
        var entryUrl = $"{list}/{id}";
        var checkInUrl = entryUrl + "/check-in";
        Guid alternativeRaceId;
        Guid categoryId;
        Guid waveId;
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
            db.OrganizerMemberships.Add(new OrganizerMembership { OrganizerId = organizerId,
                UserId = administrator.Id, Role = OrganizerRole.EventAdministrator });
            (await db.Events.SingleAsync(x => x.Id == eventId)).Status = EventStatus.RegistrationOpen;
            var baseRace = await db.Races.SingleAsync(x => x.Id == raceId);
            var alternativeRace = new Race { EventId = eventId, CourseId = baseRace.CourseId,
                Name = "Alternate", StartAtUtc = baseRace.StartAtUtc, DistanceKm = baseRace.DistanceKm };
            var category = new RaceCategory { RaceId = raceId, Name = "Alternate" };
            var wave = new StartWave { RaceId = raceId, Name = "Early", Sequence = 1, StartAtUtc = baseRace.StartAtUtc };
            db.AddRange(alternativeRace, category, wave);
            await db.SaveChangesAsync();
            alternativeRaceId = alternativeRace.Id;
            categoryId = category.Id;
            waveId = wave.Id;
        }

        using var unsigned = await anonymous.PutAsJsonAsync(checkInUrl, new { version = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        using var forbidden = await outside.PutAsJsonAsync(checkInUrl, new { version = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var forbiddenSearch = await outside.GetAsync(list + "/check-in");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenSearch.StatusCode);
        using var wrongEvent = await first.PutAsJsonAsync($"/api/v1/events/{otherEventId}/registrations/{id}/check-in", new { version = 1 });
        Assert.Equal(HttpStatusCode.NotFound, wrongEvent.StatusCode);

        foreach (var term in new[] { "0007", "Lina", "Fixture", "Lina Fixture" })
        {
            var result = await first.GetFromJsonAsync<JsonElement>($"{list}/check-in?search={Uri.EscapeDataString(term)}");
            var row = Assert.Single(result.GetProperty("items").EnumerateArray());
            Assert.Equal(id, row.GetProperty("id").GetGuid());
            Assert.Equal("0007", row.GetProperty("bibNumber").GetString());
            Assert.False(row.TryGetProperty("email", out _));
            Assert.False(row.TryGetProperty("runner", out _));
        }

        var responses = await Task.WhenAll(first.PutAsJsonAsync(checkInUrl, new { version = 1 }),
            second.PutAsJsonAsync(checkInUrl, new { version = 1 }));
        using var winner = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        using var stale = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        var checkedIn = await winner.Content.ReadFromJsonAsync<JsonElement>();
        var timestamp = checkedIn.GetProperty("checkedInAtUtc").GetDateTime();
        Assert.Equal(DateTimeKind.Utc, timestamp.Kind);
        Assert.Equal(2, checkedIn.GetProperty("version").GetInt64());
        Assert.Equal("registration_version_conflict",
            (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var repeated = await second.PutAsJsonAsync(checkInUrl, new { version = 2 });
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var repeatedRow = await repeated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(timestamp, repeatedRow.GetProperty("checkedInAtUtc").GetDateTime());
        Assert.Equal(2, repeatedRow.GetProperty("version").GetInt64());

        var current = input with { version = 2 };
        foreach (var update in new[] { current with { raceId = alternativeRaceId }, current with { bibNumber = "0010" },
            current with { categoryId = categoryId }, current with { waveId = waveId },
            current with { status = RegistrationStatus.Pending }, current with { status = RegistrationStatus.Cancelled } })
        {
            using var blocked = await first.PutAsJsonAsync(entryUrl, update);
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.Contains("Reverse check-in", await blocked.Content.ReadAsStringAsync());
        }
        using var bib = await first.PutAsJsonAsync(entryUrl + "/bib", new { version = 2, bibNumber = "0010" });
        using var withdrawal = await first.PostAsJsonAsync(entryUrl + "/cancel", new { version = 2 });
        Assert.Equal(HttpStatusCode.Conflict, bib.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, withdrawal.StatusCode);

        using var harmlessEdit = await first.PutAsJsonAsync(entryUrl, current with { team = "Club" });
        Assert.Equal(HttpStatusCode.OK, harmlessEdit.StatusCode);
        var harmlessRow = await harmlessEdit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(timestamp, harmlessRow.GetProperty("checkedInAtUtc").GetDateTime());
        Assert.Equal((int)PaymentStatus.Unpaid, harmlessRow.GetProperty("paymentStatus").GetInt32());
        Assert.Equal((int)RegistrationStatus.Confirmed, harmlessRow.GetProperty("status").GetInt32());
        Assert.Equal(3, harmlessRow.GetProperty("version").GetInt64());

        using var empty = await Reverse(second, 3, "  ");
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using var staleReversal = await Reverse(second, 2, "Desk correction");
        Assert.Equal(HttpStatusCode.Conflict, staleReversal.StatusCode);
        using var reversed = await Reverse(second, 3, " Desk correction ");
        Assert.Equal(HttpStatusCode.OK, reversed.StatusCode);
        var reversedRow = await reversed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, reversedRow.GetProperty("checkedInAtUtc").ValueKind);
        Assert.Equal(4, reversedRow.GetProperty("version").GetInt64());
        using var repeatReversal = await Reverse(second, 4, "Repeated request");
        Assert.Equal(HttpStatusCode.OK, repeatReversal.StatusCode);
        Assert.Equal(4, (await repeatReversal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64());
        using var staleRepeat = await Reverse(second, 3, "Repeated request");
        Assert.Equal(HttpStatusCode.Conflict, staleRepeat.StatusCode);

        using var editAfterReversal = await first.PutAsJsonAsync(entryUrl, current with { version = 4, bibNumber = "0010" });
        Assert.Equal(HttpStatusCode.OK, editAfterReversal.StatusCode);
        var details = await first.GetFromJsonAsync<JsonElement>(entryUrl);
        var final = details.GetProperty("registration");
        Assert.Equal("0010", final.GetProperty("bibNumber").GetString());
        Assert.Equal(5, final.GetProperty("version").GetInt64());
        var history = details.GetProperty("history").EnumerateArray().ToArray();
        var recordedCheckIn = Assert.Single(history, row => row.GetProperty("action").GetString() == "Registration.CheckedIn");
        Assert.Contains(recordedCheckIn.GetProperty("actorUserId").GetString(), new[] { director.Id, administrator.Id });
        var recordedReversal = Assert.Single(history, row => row.GetProperty("action").GetString() == "Registration.CheckInReversed");
        Assert.Equal(administrator.Id, recordedReversal.GetProperty("actorUserId").GetString());
        using var metadata = JsonDocument.Parse(recordedReversal.GetProperty("metadataJson").GetString()!);
        Assert.Equal("Desk correction", metadata.RootElement.GetProperty("reason").GetString());

        async Task<HttpResponseMessage> Reverse(HttpClient client, long version, string reason)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, checkInUrl)
            {
                Content = JsonContent.Create(new { version, reason })
            };
            return await client.SendAsync(request);
        }
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task CheckInRejectsIneligibleRunnersAndClosedEvents()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account);
        var eventId = await data.CreateEventAsync(organizerId);
        using var client = await CheckInClient(account);
        var raceId = await SeedRegistrationRace(client, eventId);
        var path = $"/api/v1/events/{eventId}/registrations";
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var item = await db.Events.SingleAsync(x => x.Id == eventId);
        var race = await db.Races.SingleAsync(x => x.Id == raceId);
        var statuses = Enum.GetValues<RegistrationStatus>();
        var entries = statuses.Select((status, index) => new Registration
        {
            EventId = eventId, OrganizerId = organizerId, RaceId = raceId, RegistrationStatus = status, BibNumber = $"00{index}",
            Runner = new Runner { OrganizerId = organizerId, FirstName = "Runner", LastName = status.ToString() },
        }).ToArray();
        var missingBib = new Registration
        {
            EventId = eventId, OrganizerId = organizerId, RaceId = raceId, RegistrationStatus = RegistrationStatus.Confirmed,
            Runner = new Runner { OrganizerId = organizerId, FirstName = "Runner", LastName = "NoBib" },
        };
        db.Registrations.AddRange(entries);
        db.Registrations.Add(missingBib);
        await db.SaveChangesAsync();
        var confirmed = entries.Single(x => x.RegistrationStatus == RegistrationStatus.Confirmed);

        foreach (var status in new[] { EventStatus.Draft, EventStatus.Published, EventStatus.Completed, EventStatus.Cancelled })
        {
            item.Status = status;
            await db.SaveChangesAsync();
            using var response = await Check(confirmed);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        item.Status = EventStatus.RegistrationOpen;
        await db.SaveChangesAsync();
        foreach (var entry in entries.Where(x => x.Id != confirmed.Id))
        {
            using var response = await Check(entry);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("confirmed", await response.Content.ReadAsStringAsync());
        }
        using var noBib = await Check(missingBib);
        Assert.Equal(HttpStatusCode.Conflict, noBib.StatusCode);
        Assert.Contains("Assign a bib", await noBib.Content.ReadAsStringAsync());
        race.Status = RecordStatus.Inactive;
        await db.SaveChangesAsync();
        using var inactive = await Check(confirmed);
        Assert.Equal(HttpStatusCode.Conflict, inactive.StatusCode);
        Assert.Contains("inactive", await inactive.Content.ReadAsStringAsync());
        Assert.All(await db.Registrations.AsNoTracking().Where(x => x.EventId == eventId).ToListAsync(), entry =>
        {
            Assert.Null(entry.CheckedInAtUtc);
            Assert.Equal(1, entry.Version);
        });
        Assert.False(await db.AuditRecords.AnyAsync(x => x.OrganizerId == organizerId && x.Action == "Registration.CheckedIn"));

        Task<HttpResponseMessage> Check(Registration entry) =>
            client.PutAsJsonAsync($"{path}/{entry.Id}/check-in", new { version = entry.Version });
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task TerminalEventsRejectReversalAndPreserveCheckInAndAuditHistory()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account);
        using var client = await CheckInClient(account);

        foreach (var status in new[] { EventStatus.Completed, EventStatus.Cancelled })
        {
            var eventId = await data.CreateEventAsync(organizerId);
            var raceId = await SeedRegistrationRace(client, eventId);
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
            var item = await db.Events.SingleAsync(x => x.Id == eventId);
            item.Status = status == EventStatus.Completed ? EventStatus.Live : EventStatus.RegistrationOpen;
            var entry = new Registration
            {
                EventId = eventId, OrganizerId = organizerId, RaceId = raceId, BibNumber = "0007",
                RegistrationStatus = RegistrationStatus.Confirmed,
                Runner = new Runner { OrganizerId = organizerId, FirstName = "Lina", LastName = "Fixture" }
            };
            db.Registrations.Add(entry);
            await db.SaveChangesAsync();

            var path = $"/api/v1/events/{eventId}/registrations";
            var detailsUrl = $"{path}/{entry.Id}";
            var checkInUrl = detailsUrl + "/check-in";
            using var checkIn = await client.PutAsJsonAsync(checkInUrl, new { version = entry.Version });
            Assert.Equal(HttpStatusCode.OK, checkIn.StatusCode);
            var checkedIn = await checkIn.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(checkedIn.GetProperty("canReverse").GetBoolean());
            var timestamp = checkedIn.GetProperty("checkedInAtUtc").GetDateTime();
            var version = checkedIn.GetProperty("version").GetInt64();
            var before = await client.GetFromJsonAsync<JsonElement>(detailsUrl);
            var audit = Assert.Single(before.GetProperty("history").EnumerateArray());
            Assert.Equal("Registration.CheckedIn", audit.GetProperty("action").GetString());

            using var transition = await client.PatchAsJsonAsync($"/api/v1/events/{eventId}/status", new { status });
            Assert.True(transition.IsSuccessStatusCode);
            var search = await client.GetFromJsonAsync<JsonElement>(path + "/check-in?search=0007");
            var row = Assert.Single(search.GetProperty("items").EnumerateArray());
            Assert.False(row.GetProperty("canReverse").GetBoolean());

            using var request = new HttpRequestMessage(HttpMethod.Delete, checkInUrl)
            {
                Content = JsonContent.Create(new { version, reason = "Desk correction" })
            };
            using var reversal = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, reversal.StatusCode);
            Assert.Contains("Registration changes are closed", await reversal.Content.ReadAsStringAsync());

            var after = await client.GetFromJsonAsync<JsonElement>(detailsUrl);
            var registration = after.GetProperty("registration");
            Assert.Equal(timestamp, registration.GetProperty("checkedInAtUtc").GetDateTime());
            Assert.Equal(version, registration.GetProperty("version").GetInt64());
            Assert.Equal(before.GetProperty("history").GetRawText(), after.GetProperty("history").GetRawText());
        }
    }

    private async Task<HttpClient> CheckInClient(TestAccount account)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));
        return client;
    }
}
