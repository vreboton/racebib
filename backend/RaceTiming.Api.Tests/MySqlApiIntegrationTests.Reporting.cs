using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CsvHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RaceTiming.Application;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Persistence;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed partial class MySqlApiIntegrationTests
{
    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task ReadinessAggregatesAllStatusesAndHistoricalRacesWithoutWrites()
    {
        await using var data = new MySqlTestData(Services);
        var director = await data.CreateAccountAsync();
        var outsider = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(director, OrganizerRole.RaceDirector);
        await data.CreateOrganizerAsync(outsider);
        var eventId = await data.CreateEventAsync(organizerId);
        var emptyId = await data.CreateEventAsync(organizerId);
        using var client = await CheckInClient(director);
        using var outside = await CheckInClient(outsider);
        using var anonymous = Factory.CreateClient();
        var raceId = await SeedRegistrationRace(client, eventId);
        var url = $"/api/v1/events/{eventId}/readiness";
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var active = await db.Races.SingleAsync(x => x.Id == raceId);
        var inactive = new Race { EventId = eventId, CourseId = active.CourseId, Name = "Retired",
            StartAtUtc = active.StartAtUtc, DistanceKm = active.DistanceKm, Status = RecordStatus.Inactive };
        var emptyRace = new Race { EventId = eventId, CourseId = active.CourseId, Name = "Empty",
            StartAtUtc = active.StartAtUtc, DistanceKm = active.DistanceKm };
        var emptyInactiveRace = new Race { EventId = eventId, CourseId = active.CourseId, Name = "Empty retired race",
            StartAtUtc = active.StartAtUtc, DistanceKm = active.DistanceKm, Status = RecordStatus.Inactive };
        var withdrawnOnlyRace = new Race { EventId = eventId, CourseId = active.CourseId, Name = "Retired with withdrawals",
            StartAtUtc = active.StartAtUtc, DistanceKm = active.DistanceKm, Status = RecordStatus.Inactive };
        var category = new RaceCategory { RaceId = inactive.Id, Name = "Retired category", Status = RecordStatus.Inactive };
        var checkedIn = BulkRunner(eventId, organizerId, raceId, "Checked", 1, RegistrationStatus.Confirmed, "0007");
        checkedIn.CheckedInAtUtc = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc);
        var historicalCategory = BulkRunner(eventId, organizerId, inactive.Id, "Retired", 2, RegistrationStatus.Confirmed, "BAD BIB");
        historicalCategory.RaceCategoryId = category.Id;
        db.AddRange(inactive, emptyRace, emptyInactiveRace, withdrawnOnlyRace, category, checkedIn, historicalCategory,
            BulkRunner(eventId, organizerId, raceId, "Bibless Confirmed", 3, RegistrationStatus.Confirmed),
            BulkRunner(eventId, organizerId, raceId, "Pending", 4),
            BulkRunner(eventId, organizerId, raceId, "Waitlisted", 5, RegistrationStatus.Waitlisted),
            BulkRunner(eventId, organizerId, withdrawnOnlyRace.Id, "Withdrawn", 6, RegistrationStatus.Cancelled, "0009"),
            BulkRunner(eventId, organizerId, raceId, "DNF", 7, RegistrationStatus.Dnf),
            BulkRunner(eventId, organizerId, raceId, "DNS", 8, RegistrationStatus.Dns),
            BulkRunner(eventId, organizerId, raceId, "DQ", 9, RegistrationStatus.Disqualified));
        var eventRow = await db.Events.SingleAsync(x => x.Id == eventId);
        eventRow.Status = EventStatus.Completed;
        await db.SaveChangesAsync();

        var before = await BulkState(db, eventId);
        var auditsBefore = await db.AuditRecords.CountAsync(x => x.OrganizerId == organizerId);
        using var forbidden = await outside.GetAsync(url);
        using var unsigned = await anonymous.GetAsync(url);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);

        var report = await client.GetFromJsonAsync<JsonElement>(url);
        var summary = report.GetProperty("summary");
        var expected = new Dictionary<string, int>
        {
            ["total"] = 9, ["confirmed"] = 3, ["pending"] = 1, ["waitlisted"] = 1, ["withdrawn"] = 1,
            ["historical"] = 3, ["confirmedWithoutBib"] = 1, ["bibsAssigned"] = 3,
            ["confirmedCheckedIn"] = 1, ["confirmedNotCheckedIn"] = 2, ["inactiveRacesWithRegistrations"] = 2,
            ["inactiveRaceConfirmed"] = 1, ["eligibleWithoutBib"] = 3, ["invalidBibs"] = 1, ["duplicateBibs"] = 0,
        };
        foreach (var (field, value) in expected) Assert.Equal(value, summary.GetProperty(field).GetInt32());
        var assessment = report.GetProperty("assessment");
        Assert.Equal("actionRequired", assessment.GetProperty("level").GetString());
        Assert.Contains(assessment.GetProperty("warnings").EnumerateArray(),
            warning => warning.GetProperty("level").GetString() == "attention");
        Assert.DoesNotContain("\"Checked\"", report.GetRawText(), StringComparison.Ordinal);
        var races = report.GetProperty("races").EnumerateArray().ToArray();
        Assert.Equal(5, races.Length);
        Assert.Equal(7, RaceCounts(raceId).GetProperty("total").GetInt32());
        Assert.Equal(0, RaceCounts(emptyRace.Id).GetProperty("total").GetInt32());
        Assert.Equal("attention", RaceReport(emptyRace.Id).GetProperty("assessment").GetProperty("level").GetString());

        var emptyInactive = RaceReport(emptyInactiveRace.Id);
        Assert.False(emptyInactive.GetProperty("active").GetBoolean());
        Assert.Equal(0, RaceCounts(emptyInactiveRace.Id).GetProperty("total").GetInt32());
        Assert.Equal(0, RaceCounts(emptyInactiveRace.Id).GetProperty("inactiveRacesWithRegistrations").GetInt32());
        var emptyInactiveAssessment = emptyInactive.GetProperty("assessment");
        Assert.Equal("ready", emptyInactiveAssessment.GetProperty("level").GetString());
        Assert.Empty(emptyInactiveAssessment.GetProperty("warnings").EnumerateArray());

        var withdrawnCounts = RaceCounts(withdrawnOnlyRace.Id);
        Assert.Equal(1, withdrawnCounts.GetProperty("total").GetInt32());
        Assert.Equal(1, withdrawnCounts.GetProperty("withdrawn").GetInt32());
        Assert.Equal(0, withdrawnCounts.GetProperty("confirmed").GetInt32());
        Assert.Equal(1, withdrawnCounts.GetProperty("inactiveRacesWithRegistrations").GetInt32());
        Assert.Equal("attention", RaceReport(withdrawnOnlyRace.Id).GetProperty("assessment").GetProperty("level").GetString());

        var confirmedCounts = RaceCounts(inactive.Id);
        Assert.Equal(1, confirmedCounts.GetProperty("confirmed").GetInt32());
        Assert.Equal(1, confirmedCounts.GetProperty("inactiveRacesWithRegistrations").GetInt32());
        Assert.Equal("actionRequired", RaceReport(inactive.Id).GetProperty("assessment").GetProperty("level").GetString());
        var inactiveWarning = Assert.Single(assessment.GetProperty("warnings").EnumerateArray(),
            warning => warning.GetProperty("label").GetString() == "Inactive races with registrations");
        Assert.Equal(2, inactiveWarning.GetProperty("count").GetInt32());
        foreach (var (field, value) in expected)
            Assert.Equal(value, races.Sum(race => race.GetProperty("counts").GetProperty(field).GetInt32()));

        var empty = await client.GetFromJsonAsync<JsonElement>($"/api/v1/events/{emptyId}/readiness");
        Assert.Equal(0, empty.GetProperty("summary").GetProperty("total").GetInt32());
        Assert.Empty(empty.GetProperty("races").EnumerateArray());
        Assert.Equal("attention", empty.GetProperty("assessment").GetProperty("level").GetString());
        Assert.Equal(before, await BulkState(db, eventId));
        Assert.Equal(auditsBefore, await db.AuditRecords.CountAsync(x => x.OrganizerId == organizerId));

        JsonElement RaceReport(Guid id) => Assert.Single(races, race => race.GetProperty("id").GetGuid() == id);
        JsonElement RaceCounts(Guid id) => RaceReport(id).GetProperty("counts");
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task RosterExportIsScopedFilteredDeterministicAndSpreadsheetSafe()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var outsider = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account);
        await data.CreateOrganizerAsync(outsider);
        var eventId = await data.CreateEventAsync(organizerId);
        var otherId = await data.CreateEventAsync(organizerId);
        using var client = await CheckInClient(account);
        using var outside = await CheckInClient(outsider);
        var raceId = await SeedRegistrationRace(client, eventId);
        var otherEventRace = await SeedRegistrationRace(client, otherId);
        var path = $"/api/v1/events/{eventId}/registrations/export";
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var active = await db.Races.SingleAsync(x => x.Id == raceId);
        var secondary = new Race { EventId = eventId, CourseId = active.CourseId, Name = "Zulu",
            StartAtUtc = active.StartAtUtc, DistanceKm = active.DistanceKm };
        var completed = await db.Events.SingleAsync(x => x.Id == eventId);
        completed.Status = EventStatus.Cancelled;
        var assigned = BulkRunner(eventId, organizerId, raceId, "Bravo", 2, RegistrationStatus.Confirmed, "0007");
        assigned.Runner.FirstName = "Jo, \"José\"";
        assigned.Runner.Email = "private@example.test";
        assigned.Runner.MobileNumber = "555-hidden";
        assigned.Runner.EmergencyContact = "Hidden contact";
        assigned.Runner.Birthdate = new DateOnly(1988, 2, 3);
        assigned.BibDisplayName = "  =1+1";
        assigned.CheckedInAtUtc = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc);
        assigned.Team = "Trail Team\nNorth";
        var unassigned = BulkRunner(eventId, organizerId, raceId, "Alpha", 1, RegistrationStatus.Confirmed);
        var withdrawn = BulkRunner(eventId, organizerId, secondary.Id, "Delta", 3, RegistrationStatus.Cancelled, "-7");
        withdrawn.Team = "  @review";
        db.AddRange(secondary, assigned, unassigned, withdrawn, BulkRunner(otherId, organizerId, otherEventRace, "Other event", 4));
        await db.SaveChangesAsync();
        var before = await BulkState(db, eventId);
        var auditBefore = await db.AuditRecords.CountAsync(x => x.OrganizerId == organizerId);

        using var forbidden = await outside.GetAsync(path);
        using var wrongRace = await client.GetAsync($"{path}?raceId={otherEventRace}");
        using var invalidFilter = await client.GetAsync(path + "?status=999");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongRace.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidFilter.StatusCode);

        using var export = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType?.MediaType);
        Assert.True(export.Headers.CacheControl?.NoStore);
        Assert.Contains($"racebib-roster-{eventId:D}-", export.Content.Headers.ContentDisposition?.FileName);
        var bytes = await export.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal(bytes, await client.GetByteArrayAsync(path));
        var rows = ReadRoster(bytes);
        Assert.Equal(new[] { "Bib Number", "First Name", "Last Name", "Name on Bib", "Race", "Registration Status",
            "Check-in Status", "Check-in Time (UTC)", "Team" }, rows[0]);
        Assert.Equal(new[] { "Alpha", "Bravo", "Delta" }, rows.Skip(1).Select(row => row[2]));
        Assert.Equal(new[] { "0007", "Jo, \"José\"", "Bravo", "'=1+1", "10K", "Confirmed",
            "Checked in", "2026-01-01T06:00:00.0000000Z", "Trail Team\nNorth" }, rows[2]);
        Assert.Equal("Runner Alpha", rows[1][3]);
        Assert.Equal("Not checked in", rows[1][6]);
        Assert.Equal("'-7", rows[3][0]);
        Assert.Equal("Withdrawn", rows[3][5]);
        Assert.Equal("'  @review", rows[3][8]);
        var raw = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("private@example.test", raw);
        Assert.DoesNotContain("555-hidden", raw);
        Assert.DoesNotContain("Hidden contact", raw);
        Assert.DoesNotContain("1988", raw);
        Assert.DoesNotContain("Paid", raw);

        var filtered = ReadRoster(await client.GetByteArrayAsync($"{path}?raceId={raceId}&status=Confirmed&bib=Assigned"));
        Assert.Equal("0007", Assert.Single(filtered.Skip(1))[0]);
        var blanks = ReadRoster(await client.GetByteArrayAsync(path + "?bib=Unassigned"));
        Assert.Equal("Alpha", Assert.Single(blanks.Skip(1))[2]);
        var cancelled = ReadRoster(await client.GetByteArrayAsync(path + "?status=Cancelled"));
        Assert.Equal("Delta", Assert.Single(cancelled.Skip(1))[2]);
        Assert.Equal(before, await BulkState(db, eventId));
        Assert.Equal(auditBefore, await db.AuditRecords.CountAsync(x => x.OrganizerId == organizerId));
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task RosterExportRejectsOversizedSelectionsAndAllowsTheExactLimit()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account);
        var eventId = await data.CreateEventAsync(organizerId);
        using var client = await CheckInClient(account);
        var raceId = await SeedRegistrationRace(client, eventId);
        var path = $"/api/v1/events/{eventId}/registrations/export";
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        db.Registrations.AddRange(Enumerable.Range(0, RosterCsv.MaxRows + 1).Select(index =>
            BulkRunner(eventId, organizerId, raceId, $"Runner {index:D5}", 1,
                index == RosterCsv.MaxRows ? RegistrationStatus.Pending : RegistrationStatus.Confirmed)));
        await db.SaveChangesAsync();

        using var oversized = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, oversized.StatusCode);
        Assert.Contains("5,000", await oversized.Content.ReadAsStringAsync());
        var allowed = ReadRoster(await client.GetByteArrayAsync(path + "?status=Confirmed"));
        Assert.Equal(RosterCsv.MaxRows + 1, allowed.Count);
        Assert.Equal("Runner 00000", allowed[1][2]);
        Assert.Equal($"Runner {RosterCsv.MaxRows - 1:D5}", allowed[^1][2]);
    }

    private static List<string[]> ReadRoster(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        var rows = new List<string[]>();
        while (csv.Read()) rows.Add(csv.Parser.Record!.ToArray());
        return rows;
    }
}
