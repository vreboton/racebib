using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsvHelper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Application;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;

namespace RaceTiming.Api.Controllers;

public sealed partial class RegistrationsController
{
    private const int ReadinessRaceLimit = 1000;
    private const string ValidBibPattern = @"\A[A-Za-z0-9_-]{1,32}\z";
    private static readonly string[] RosterColumns =
    [
        "Bib Number", "First Name", "Last Name", "Name on Bib", "Race", "Registration Status",
        "Check-in Status", "Check-in Time (UTC)", "Team"
    ];

    public enum RosterBibFilter { Assigned, Unassigned }

    [HttpGet("/api/v1/events/{eventId:guid}/readiness")]
    public Task<IActionResult> Readiness(Guid eventId, CancellationToken ct) =>
        WithEvent(eventId, false, ct, async _ =>
        {
            var races = await db.Races.AsNoTracking().Where(x => x.EventId == eventId)
                .OrderBy(x => x.Name).ThenBy(x => x.Id).Take(ReadinessRaceLimit + 1)
                .Select(x => new { x.Id, x.Name, x.Status }).ToListAsync(ct);
            if (races.Count > ReadinessRaceLimit) return UnprocessableEntity("Readiness supports up to 1,000 races per event.");

            var entries = db.Registrations.AsNoTracking().Where(x => x.EventId == eventId);
            var duplicates = entries.Where(x => x.BibNumber != null).GroupBy(x => x.BibNumber)
                .Where(group => group.Count() > 1).Select(group => group.Key);
            var grouped = await entries.GroupBy(x => x.RaceId).Select(group => new
            {
                RaceId = group.Key,
                Total = group.Count(),
                Confirmed = group.Count(x => x.RegistrationStatus == RegistrationStatus.Confirmed),
                Pending = group.Count(x => x.RegistrationStatus == RegistrationStatus.Pending),
                Waitlisted = group.Count(x => x.RegistrationStatus == RegistrationStatus.Waitlisted),
                Withdrawn = group.Count(x => x.RegistrationStatus == RegistrationStatus.Cancelled),
                Historical = group.Count(x => x.RegistrationStatus == RegistrationStatus.Dnf ||
                    x.RegistrationStatus == RegistrationStatus.Dns || x.RegistrationStatus == RegistrationStatus.Disqualified),
                ConfirmedWithoutBib = group.Count(x => x.RegistrationStatus == RegistrationStatus.Confirmed && x.BibNumber == null),
                BibsAssigned = group.Count(x => x.BibNumber != null),
                ConfirmedCheckedIn = group.Count(x => x.RegistrationStatus == RegistrationStatus.Confirmed && x.CheckedInAtUtc != null),
                EligibleWithoutBib = group.Count(x => x.BibNumber == null && x.CheckedInAtUtc == null &&
                    (x.RegistrationStatus == RegistrationStatus.Confirmed || x.RegistrationStatus == RegistrationStatus.Pending ||
                     x.RegistrationStatus == RegistrationStatus.Waitlisted)),
                PendingOrWaitlistedEligibleWithoutBib = group.Count(x => x.BibNumber == null && x.CheckedInAtUtc == null &&
                    (x.RegistrationStatus == RegistrationStatus.Pending || x.RegistrationStatus == RegistrationStatus.Waitlisted)),
                InvalidBibs = group.Count(x => x.BibNumber != null &&
                    !Regex.IsMatch(EF.Functions.Collate(x.BibNumber, "utf8mb4_0900_as_cs"), ValidBibPattern)),
                DuplicateBibs = group.Count(x => x.BibNumber != null && duplicates.Contains(x.BibNumber))
            }).ToDictionaryAsync(x => x.RaceId, ct);

            var breakdown = races.Select(race =>
            {
                var active = race.Status == RecordStatus.Active;
                var counts = grouped.TryGetValue(race.Id, out var row) ? new ReadinessCounts(
                    row.Total, row.Confirmed, row.Pending, row.Waitlisted, row.Withdrawn, row.Historical,
                    row.ConfirmedWithoutBib, row.BibsAssigned, row.ConfirmedCheckedIn, row.Confirmed - row.ConfirmedCheckedIn,
                    !active && row.Total > 0 ? 1 : 0, active ? 0 : row.Confirmed, active ? row.EligibleWithoutBib : 0,
                    active ? row.PendingOrWaitlistedEligibleWithoutBib : 0, row.InvalidBibs, row.DuplicateBibs) : new();
                return new { race.Id, race.Name, active, counts, assessment = counts.Assess(expectConfirmed: active || counts.Total > 0) };
            }).ToArray();
            var summary = ReadinessCounts.Sum(breakdown.Select(x => x.counts).ToArray());
            return Ok(new { summary, assessment = summary.Assess(), races = breakdown });
        });

    [HttpGet("export")]
    public Task<IActionResult> ExportRoster(Guid eventId, Guid? raceId, RegistrationStatus? status, RosterBibFilter? bib,
        CancellationToken ct) => WithEvent(eventId, false, ct, async _ =>
    {
        if ((status.HasValue && !Enum.IsDefined(status.Value)) || (bib.HasValue && !Enum.IsDefined(bib.Value)))
            return BadRequest("Choose a valid registration status and bib filter.");
        if (raceId.HasValue && !await db.Races.AnyAsync(x => x.EventId == eventId && x.Id == raceId, ct))
            return BadRequest("Choose a race from this event.");

        var query = db.Registrations.AsNoTracking().Where(x => x.EventId == eventId);
        if (raceId.HasValue) query = query.Where(x => x.RaceId == raceId);
        if (status.HasValue) query = query.Where(x => x.RegistrationStatus == status);
        if (bib.HasValue)
            query = bib == RosterBibFilter.Assigned ? query.Where(x => x.BibNumber != null) : query.Where(x => x.BibNumber == null);
        var rows = await query.OrderBy(x => x.Race.Name).ThenBy(x => x.RaceId)
            .ThenBy(x => x.Runner.LastName).ThenBy(x => x.Runner.FirstName).ThenBy(x => x.Id)
            .Take(RosterCsv.MaxRows + 1).Select(x => new
            {
                x.BibNumber, x.Runner.FirstName, x.Runner.LastName, x.BibDisplayName, Race = x.Race.Name,
                x.RegistrationStatus, x.CheckedInAtUtc, x.Team
            }).ToListAsync(ct);
        if (rows.Count > RosterCsv.MaxRows)
            return UnprocessableEntity("The roster exceeds 5,000 registrations. Narrow the race, status, or bib filter.");

        using var buffer = new MemoryStream();
        using (var writer = new StreamWriter(buffer, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true))
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            foreach (var header in RosterColumns) csv.WriteField(header);
            csv.NextRecord();
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                string?[] fields =
                [
                    row.BibNumber, row.FirstName, row.LastName,
                    Registration.DisplayName(row.BibDisplayName, row.FirstName, row.LastName), row.Race,
                    RosterCsv.StatusLabel(row.RegistrationStatus), row.CheckedInAtUtc.HasValue ? "Checked in" : "Not checked in",
                    row.CheckedInAtUtc is { } timestamp ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
                        .ToString("O", CultureInfo.InvariantCulture) : null, row.Team
                ];
                foreach (var field in fields) csv.WriteField(RosterCsv.SafeField(field));
                csv.NextRecord();
            }
        }
        Response.Headers.CacheControl = "no-store";
        return File(buffer.ToArray(), "text/csv; charset=utf-8", $"racebib-roster-{eventId:D}-{DateTime.UtcNow:yyyy-MM-dd}.csv");
    });
}
