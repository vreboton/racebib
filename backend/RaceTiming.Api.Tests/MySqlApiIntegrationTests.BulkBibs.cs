using System.Net;
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
    public async Task BulkBibPreviewIsReadOnlyAndConfirmationPreservesAllReservedBibs()
    {
        await using var data = new MySqlTestData(Services);
        var director = await data.CreateAccountAsync();
        var outsider = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(director, OrganizerRole.RaceDirector);
        await data.CreateOrganizerAsync(outsider);
        var eventId = await data.CreateEventAsync(organizerId);
        var otherEvent = await data.CreateEventAsync(organizerId);
        using var client = await CheckInClient(director);
        using var outside = await CheckInClient(outsider);
        using var anonymous = Factory.CreateClient();
        var raceId = await SeedRegistrationRace(client, eventId);
        var path = $"/api/v1/events/{eventId}/registrations/bibs";

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var activeRace = await db.Races.SingleAsync(x => x.Id == raceId);
        var otherRace = new Race { EventId = eventId, CourseId = activeRace.CourseId, Name = "Other",
            StartAtUtc = activeRace.StartAtUtc, DistanceKm = activeRace.DistanceKm };
        var inactiveRace = new Race { EventId = eventId, CourseId = activeRace.CourseId, Name = "Retired",
            StartAtUtc = activeRace.StartAtUtc, DistanceKm = activeRace.DistanceKm, Status = RecordStatus.Inactive };
        db.AddRange(otherRace, inactiveRace);
        var pending = BulkRunner(eventId, organizerId, raceId, "Pending", 1);
        var confirmed = BulkRunner(eventId, organizerId, raceId, "Confirmed", 2, RegistrationStatus.Confirmed);
        var waitlisted = BulkRunner(eventId, organizerId, raceId, "Waitlisted", 3, RegistrationStatus.Waitlisted);
        var checkedIn = BulkRunner(eventId, organizerId, raceId, "CheckedIn", 4, RegistrationStatus.Confirmed, "003");
        checkedIn.CheckedInAtUtc = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc);
        var existing = BulkRunner(eventId, organizerId, raceId, "Existing", 5, bib: "005");
        var withdrawn = BulkRunner(eventId, organizerId, otherRace.Id, "Withdrawn", 6, RegistrationStatus.Cancelled, "002");
        var occupied = BulkRunner(eventId, organizerId, otherRace.Id, "Occupied", 7, bib: "001");
        var retired = BulkRunner(eventId, organizerId, inactiveRace.Id, "Retired", 8);
        var historical = new[] { RegistrationStatus.Dnf, RegistrationStatus.Dns, RegistrationStatus.Disqualified }
            .Select((status, index) => BulkRunner(eventId, organizerId, raceId, status.ToString(), index + 9, status)).ToArray();
        db.Registrations.AddRange(waitlisted, checkedIn, existing, withdrawn, occupied, retired, confirmed, pending);
        db.Registrations.AddRange(historical);
        await db.SaveChangesAsync();
        var before = await BulkState(db, eventId);
        var auditBefore = await db.AuditRecords.CountAsync(x => x.OrganizerId == organizerId);
        var input = new { raceId, start = 1, end = 9, width = 3 };

        using var forbidden = await outside.PostAsJsonAsync(path + "/preview", input);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var unauthorized = await anonymous.PostAsJsonAsync(path + "/preview", input);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var allRaces = await BulkPreview(client, path, new { start = 1, end = 9, width = 3 });
        Assert.Equal(11, allRaces.GetProperty("scopeCount").GetInt32());
        Assert.Contains(allRaces.GetProperty("rows").EnumerateArray(),
            row => row.GetProperty("skipReason").GetString() == "Race is inactive.");
        var preview = await BulkPreview(client, path, input);
        var again = await BulkPreview(client, path, input);
        Assert.Equal(8, preview.GetProperty("scopeCount").GetInt32());
        Assert.Equal(3, preview.GetProperty("eligibleCount").GetInt32());
        Assert.Equal(5, preview.GetProperty("skippedCount").GetInt32());
        Assert.Equal(3, preview.GetProperty("changeCount").GetInt32());
        Assert.Equal(preview.GetProperty("rows").GetRawText(), again.GetProperty("rows").GetRawText());
        var proposals = preview.GetProperty("rows").EnumerateArray().Where(row => row.GetProperty("proposedBib").ValueKind == JsonValueKind.String).ToArray();
        Assert.Equal(new[] { pending.Id, confirmed.Id, waitlisted.Id }, proposals.Select(row => row.GetProperty("registrationId").GetGuid()));
        Assert.Equal(new[] { "004", "006", "007" }, proposals.Select(row => row.GetProperty("proposedBib").GetString()));
        Assert.Contains(preview.GetProperty("rows").EnumerateArray(),
            row => row.GetProperty("skipReason").GetString() == "Already checked in.");
        Assert.Equal(3, preview.GetProperty("rows").EnumerateArray().Count(row => row.GetProperty("skipReason").GetString() == "Historical status."));

        var exhausted = await BulkPreview(client, path, input with { end = 4 });
        Assert.Equal(0, exhausted.GetProperty("changeCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, exhausted.GetProperty("token").ValueKind);
        Assert.Contains("no available numbers", exhausted.GetProperty("error").GetString());
        var invalid = await BulkPreview(client, path, input with { width = 11 });
        Assert.Equal(JsonValueKind.Null, invalid.GetProperty("token").ValueKind);
        Assert.Contains("valid nonnegative bib range", invalid.GetProperty("error").GetString());
        Assert.Equal(before, await BulkState(db, eventId));
        Assert.Equal(auditBefore, await db.AuditRecords.CountAsync(x => x.OrganizerId == organizerId));

        using var outsideConfirm = await ConfirmBulk(outside, path, preview);
        Assert.Equal(HttpStatusCode.Forbidden, outsideConfirm.StatusCode);
        using var wrongEvent = await ConfirmBulk(client, $"/api/v1/events/{otherEvent}/registrations/bibs", preview);
        Assert.Equal(HttpStatusCode.Conflict, wrongEvent.StatusCode);
        using var applied = await ConfirmBulk(client, path, preview);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var receipt = await applied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, receipt.GetProperty("assignedCount").GetInt32());
        Assert.Equal(5, receipt.GetProperty("skippedCount").GetInt32());
        using var replay = await ConfirmBulk(client, path, preview);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);

        var after = await BulkState(db, eventId);
        var approved = proposals.ToDictionary(row => row.GetProperty("registrationId").GetGuid(), row => row.GetProperty("proposedBib").GetString());
        Assert.Equal(before.Select(row => approved.TryGetValue(row.Id, out var bib)
            ? row with { BibNumber = bib, Version = row.Version + 1 } : row), after);
        var audits = await db.AuditRecords.Where(x => x.OrganizerId == organizerId && x.Action == "Registration.BibAssigned").ToListAsync();
        Assert.Equal(3, audits.Count);
        Assert.Equal(approved.Keys.Order(), audits.Select(x => Guid.Parse(x.TargetEntityId)).Order());
        Assert.All(audits, audit =>
        {
            Assert.Equal(director.Id, audit.ActorUserId);
            Assert.Equal("Registration", audit.TargetEntityType);
            using var metadata = JsonDocument.Parse(audit.MetadataJson!);
            Assert.Equal(eventId, metadata.RootElement.GetProperty("eventId").GetGuid());
            Assert.Single(metadata.RootElement.EnumerateObject());
        });
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task BulkBibConfirmationRejectsOperationalChangesAndSerializesCompetingWriters()
    {
        await using var data = new MySqlTestData(Services);
        var director = await data.CreateAccountAsync();
        var secondDirector = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(director, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizerId);
        using var client = await CheckInClient(director);
        using var second = await CheckInClient(secondDirector);
        var raceId = await SeedRegistrationRace(client, eventId);
        var path = $"/api/v1/events/{eventId}/registrations";
        var bulk = path + "/bibs";
        var input = new { raceId, start = 10, end = 19, width = 3 };
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var item = await db.Events.SingleAsync(x => x.Id == eventId);
        var race = await db.Races.SingleAsync(x => x.Id == raceId);
        item.Status = EventStatus.RegistrationOpen;
        db.OrganizerMemberships.Add(new OrganizerMembership { OrganizerId = organizerId, UserId = secondDirector.Id, Role = OrganizerRole.RaceDirector });
        var manual = BulkRunner(eventId, organizerId, raceId, "Manual", 1);
        var checkIn = BulkRunner(eventId, organizerId, raceId, "CheckIn", 2, RegistrationStatus.Confirmed, "006");
        var withdrawn = BulkRunner(eventId, organizerId, raceId, "Withdrawal", 3);
        var target = BulkRunner(eventId, organizerId, raceId, "Target", 4);
        db.AddRange(manual, checkIn, withdrawn, target);
        await db.SaveChangesAsync();

        var manualPreview = await BulkPreview(client, bulk, input);
        using var manualEdit = await second.PutAsJsonAsync($"{path}/{manual.Id}/bib", new { version = 1, bibNumber = "015" });
        Assert.Equal(HttpStatusCode.NoContent, manualEdit.StatusCode);
        await AssertStale(manualPreview);

        var checkInPreview = await BulkPreview(client, bulk, input);
        using var checkedIn = await second.PutAsJsonAsync($"{path}/{checkIn.Id}/check-in", new { version = 1 });
        Assert.Equal(HttpStatusCode.OK, checkedIn.StatusCode);
        await AssertStale(checkInPreview);

        var withdrawalPreview = await BulkPreview(client, bulk, input);
        using var withdrawal = await second.PostAsJsonAsync($"{path}/{withdrawn.Id}/cancel", new { version = 1 });
        Assert.Equal(HttpStatusCode.NoContent, withdrawal.StatusCode);
        await AssertStale(withdrawalPreview);

        var retirementPreview = await BulkPreview(client, bulk, input);
        race.Status = RecordStatus.Inactive;
        await db.SaveChangesAsync();
        await AssertStale(retirementPreview);
        race.Status = RecordStatus.Active;
        await db.SaveChangesAsync();
        var pending = await BulkState(db, eventId);
        Assert.Null(pending.Single(row => row.Id == target.Id).BibNumber);
        Assert.Equal(1, pending.Single(row => row.Id == target.Id).Version);

        var byFirst = await BulkPreview(client, bulk, input);
        var bySecond = await BulkPreview(second, bulk, input);
        using var stolen = await ConfirmBulk(second, bulk, byFirst);
        Assert.Equal(HttpStatusCode.Conflict, stolen.StatusCode);

        const string csv = "First Name,Last Name,Email,Bib Number\nCSV,Fixture,bulk-race@example.test,010";
        var csvPreview = await PreviewRegistrationCsv(second, path, raceId, csv);
        Assert.True(csvPreview.GetProperty("valid").GetBoolean());
        using var form = ImportForm(raceId, csv, token: csvPreview.GetProperty("token").GetString());
        var contenders = await Task.WhenAll(ConfirmBulk(client, bulk, byFirst), second.PostAsync(path + "/import/confirm", form));
        using var bulkResult = contenders[0];
        using var csvResult = contenders[1];
        Assert.Single(contenders, x => x.IsSuccessStatusCode);
        Assert.Single(contenders, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Single(await BulkState(db, eventId), row => row.BibNumber == "010");
        await AssertStale(byFirst);
        using var otherStale = await ConfirmBulk(second, bulk, bySecond);
        Assert.Equal(HttpStatusCode.Conflict, otherStale.StatusCode);

        db.Registrations.Add(BulkRunner(eventId, organizerId, raceId, "Concurrent", 5));
        await db.SaveChangesAsync();
        var left = await BulkPreview(client, bulk, input);
        var right = await BulkPreview(second, bulk, input);
        var bulkContenders = await Task.WhenAll(ConfirmBulk(client, bulk, left), ConfirmBulk(second, bulk, right));
        using var leftResult = bulkContenders[0];
        using var rightResult = bulkContenders[1];
        Assert.Single(bulkContenders, x => x.IsSuccessStatusCode);
        Assert.Single(bulkContenders, x => x.StatusCode == HttpStatusCode.Conflict);

        db.Registrations.Add(BulkRunner(eventId, organizerId, raceId, "Closed", 6));
        await db.SaveChangesAsync();
        var terminalPreview = await BulkPreview(client, bulk, input);
        foreach (var status in new[] { EventStatus.Completed, EventStatus.Cancelled })
        {
            item.Status = status;
            await db.SaveChangesAsync();
            using var forbiddenPreview = await client.PostAsJsonAsync(bulk + "/preview", input);
            using var forbiddenConfirm = await ConfirmBulk(client, bulk, terminalPreview);
            Assert.Equal(HttpStatusCode.Conflict, forbiddenPreview.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, forbiddenConfirm.StatusCode);
        }

        async Task AssertStale(JsonElement preview)
        {
            using var response = await ConfirmBulk(client, bulk, preview);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("Preview again", await response.Content.ReadAsStringAsync());
        }
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task BulkBibPreviewAndConfirmationRejectTheSameMySqlCollationCollision()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account);
        var eventId = await data.CreateEventAsync(organizerId);
        using var client = await CheckInClient(account);
        var raceId = await SeedRegistrationRace(client, eventId);
        var path = $"/api/v1/events/{eventId}/registrations/bibs";
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var race = await db.Races.SingleAsync(x => x.Id == raceId);
        var otherRace = new Race { EventId = eventId, CourseId = race.CourseId, Name = "Other",
            StartAtUtc = race.StartAtUtc, DistanceKm = race.DistanceKm };
        db.AddRange(otherRace, BulkRunner(eventId, organizerId, raceId, "First", 1),
            BulkRunner(eventId, organizerId, raceId, "Second", 2));
        await db.SaveChangesAsync();
        var input = new { raceId, start = 1, end = 2, width = 3 };
        var preview = await BulkPreview(client, path, input);
        Assert.Equal(2, preview.GetProperty("changeCount").GetInt32());

        // Outside the approved scope, a legacy bib becomes equivalent to 002 under the unique index collation.
        var legacy = BulkRunner(eventId, organizerId, otherRace.Id, "Legacy", 3, bib: "００２");
        db.Add(legacy);
        db.AuditRecords.Add(new AuditRecord
        {
            OrganizerId = organizerId, ActorUserId = account.Id, TargetEntityType = "Registration",
            TargetEntityId = legacy.Id.ToString(), Action = "Registration.BibChanged"
        });
        await db.SaveChangesAsync();
        var before = await BulkState(db, eventId);
        var auditHistory = db.AuditRecords.AsNoTracking().Where(x => x.OrganizerId == organizerId).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.ActorUserId, x.ActorIdentity, x.Action, x.TargetEntityType, x.TargetEntityId,
                x.TimestampUtc, x.MetadataJson });
        var auditBefore = await auditHistory.ToListAsync();

        var refreshed = await BulkPreview(client, path, input);
        Assert.Equal(2, refreshed.GetProperty("eligibleCount").GetInt32());
        Assert.Equal(0, refreshed.GetProperty("changeCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, refreshed.GetProperty("token").ValueKind);
        Assert.All(refreshed.GetProperty("rows").EnumerateArray(),
            row => Assert.Equal(JsonValueKind.Null, row.GetProperty("proposedBib").ValueKind));
        var message = refreshed.GetProperty("error").GetString();
        Assert.Contains("already reserved", message);
        Assert.Contains("different range and preview again", message);

        using var response = await ConfirmBulk(client, path, preview);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(message, await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await BulkState(db, eventId));
        Assert.Equal(auditBefore, await auditHistory.ToListAsync());
    }

    private static Registration BulkRunner(Guid eventId, Guid organizerId, Guid raceId, string lastName, int order,
        RegistrationStatus status = RegistrationStatus.Pending, string? bib = null) => new()
    {
        EventId = eventId, OrganizerId = organizerId, RaceId = raceId, BibNumber = bib,
        RegistrationStatus = status, PaymentStatus = PaymentStatus.Paid,
        CreatedAt = new DateTime(2026, 1, 1, 0, order, 0, DateTimeKind.Utc),
        Runner = new Runner { OrganizerId = organizerId, FirstName = "Runner", LastName = lastName }
    };

    private static async Task<JsonElement> BulkPreview(HttpClient client, string path, object input)
    {
        using var response = await client.PostAsJsonAsync(path + "/preview", input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> ConfirmBulk(HttpClient client, string path, JsonElement preview) =>
        client.PostAsJsonAsync(path + "/confirm", new { token = preview.GetProperty("token").GetString() });

    private sealed record BulkSnapshot(Guid Id, string? BibNumber, long Version, RegistrationStatus Status, PaymentStatus Payment,
        DateTime? CheckedInAtUtc, Guid RaceId, Guid? CategoryId, Guid? WaveId);

    private static Task<List<BulkSnapshot>> BulkState(RaceTimingDbContext db, Guid eventId) =>
        db.Registrations.AsNoTracking().Where(x => x.EventId == eventId).OrderBy(x => x.Id)
            .Select(x => new BulkSnapshot(x.Id, x.BibNumber, x.Version, x.RegistrationStatus, x.PaymentStatus,
                x.CheckedInAtUtc, x.RaceId, x.RaceCategoryId, x.StartWaveId)).ToListAsync();
}
