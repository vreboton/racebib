using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsvHelper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Application;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;

namespace RaceTiming.Api.Controllers;

public sealed partial class RegistrationsController
{
    private const int MaxBytes = 1024 * 1024;
    private const int MaxRows = 1000;
    private static readonly string[] Columns =
    [
        "First Name", "Last Name", "Name on Bib", "Gender", "Birthdate", "Email", "Mobile Number",
        "Race", "Category", "Start Wave", "Bib Number", "Team", "Emergency Contact", "Registration Status", "Payment Status"
    ];
    private sealed record ImportRow(int Row, string? Error, string? NameOnBib = null, string? BibNumber = null);
    private sealed record ImportPlan(string[] Headers, Dictionary<string, int> Mapping,
        List<ImportRow> Rows, List<Registration> Entries, string? Error);
    private sealed record ImportTicket(Guid Id, Guid EventId, string Actor, string Hash);

    [HttpPost("import/preview")]
    [RequestSizeLimit(2 * MaxBytes)]
    public Task<IActionResult> Preview(Guid eventId, IFormFile file, [FromForm] string? mapping,
        [FromForm] string? assignment, [FromServices] IDataProtectionProvider protection, CancellationToken ct) =>
        WithEvent(eventId, false, ct, async item =>
        {
            try
            {
                var bytes = await ReadUpload(file, ct);
                var plan = await Prepare(item, bytes, mapping, assignment, ct);
                var valid = plan.Error is null && plan.Entries.Count > 0 && plan.Rows.All(x => x.Error is null);
                var token = valid ? Protector(protection).Protect(JsonSerializer.Serialize(
                    new ImportTicket(Guid.NewGuid(), eventId, Actor(), Fingerprint(bytes, mapping, assignment,
                        plan.Entries))), TimeSpan.FromMinutes(20)) : null;
                return Ok(new { plan.Headers, fields = Columns, plan.Mapping, plan.Rows, valid, token,
                    plan.Error, validCount = plan.Rows.Count(x => x.Error is null),
                    errorCsv = ErrorCsv(plan.Rows) });
            }
            catch (Exception ex) when (ex is CsvHelperException or DecoderFallbackException or JsonException or InvalidDataException)
            {
                return BadRequest("Upload a valid UTF-8 CSV within the file and row limits and select valid mapping options.");
            }
        });

    [HttpPost("import/confirm")]
    [RequestSizeLimit(2 * MaxBytes)]
    public Task<IActionResult> Confirm(Guid eventId, IFormFile file, [FromForm] string token,
        [FromForm] string? mapping, [FromForm] string? assignment, [FromServices] IDataProtectionProvider protection,
        CancellationToken ct) => WithEvent(eventId, true, ct, async item =>
    {
        try
        {
            var ticket = JsonSerializer.Deserialize<ImportTicket>(Protector(protection).Unprotect(token));
            if (ticket is null || ticket.EventId != eventId || ticket.Actor != Actor())
                return Conflict("Preview this import again before confirming.");
            var receipt = ticket.Id.ToString();
            if (await db.AuditRecords.AnyAsync(x => x.OrganizerId == item.OrganizerId
                    && x.TargetEntityType == "RegistrationImport" && x.TargetEntityId == receipt, ct))
                return Conflict("This preview was already imported.");
            var bytes = await ReadUpload(file, ct);
            var plan = await Prepare(item, bytes, mapping, assignment, ct);
            if (plan.Error is not null || plan.Entries.Count == 0 || plan.Rows.Any(x => x.Error is not null)
                || ticket.Hash != Fingerprint(bytes, mapping, assignment, plan.Entries))
                return Conflict("The file, mapping, assignment or event data changed. Preview again.");

            db.Registrations.AddRange(plan.Entries);
            foreach (var entry in plan.Entries)
            {
                Record(entry, "Registration.Imported");
                if (entry.BibNumber is not null) Record(entry, "Registration.BibChanged");
            }
            audit.Record(User, "Registration.ImportCompleted", "RegistrationImport", receipt, item.OrganizerId);
            await db.SaveChangesAsync(ct);
            return Ok(new { importedCount = plan.Entries.Count });
        }
        catch (Exception ex) when (ex is CsvHelperException or DecoderFallbackException
            or JsonException or InvalidDataException or CryptographicException)
        {
            return Conflict("The import preview is invalid or expired. Preview again.");
        }
    });

    private async Task<ImportPlan> Prepare(Event item, byte[] bytes, string? mappingJson, string? assignmentJson, CancellationToken ct)
    {
        var source = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        using var csv = new CsvReader(new StringReader(source), CultureInfo.InvariantCulture);
        if (!csv.Read()) throw new InvalidDataException();
        csv.ReadHeader();
        var headers = csv.HeaderRecord!;
        if (headers.Length > 100) throw new InvalidDataException();
        var mapping = mappingJson is null
            ? Columns.Select(key => (key, index: Array.FindIndex(headers, header => HeaderMatches(key, header))))
                .Where(x => x.index >= 0).ToDictionary(x => x.key, x => x.index)
            : JsonSerializer.Deserialize<Dictionary<string, int>>(mappingJson) ?? throw new InvalidDataException();
        var options = assignmentJson is null ? new BibAssignment()
            : JsonSerializer.Deserialize<BibAssignment>(assignmentJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException();
        var rows = new List<ImportRow>();
        var entries = new List<Registration>();
        var plan = new ImportPlan(headers, mapping, rows, entries, null);
        if (mapping.Values.Any(i => i < 0 || i >= headers.Length)
            || mapping.Values.Distinct().Count() != mapping.Count
            || mapping.Keys.Except(Columns).Any() || !Columns.Take(2).Append("Race").All(mapping.ContainsKey))
            return plan with { Error = "Map First Name, Last Name and Race to distinct columns." };

        var races = await db.Races.AsNoTracking().Include(x => x.Categories).Include(x => x.StartWaves)
            .Where(x => x.EventId == item.Id && x.Status == RecordStatus.Active).ToListAsync(ct);
        var existing = await Entries(item.Id).ToListAsync(ct);
        var runners = await db.Runners.Where(x => x.OrganizerId == item.OrganizerId).ToListAsync(ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (csv.Read())
        {
            if (rows.Count >= MaxRows) throw new InvalidDataException();
            string? Value(string key) => mapping.TryGetValue(key, out var index) ? Optional(csv.GetField(index)) : null;
            string? error;
            Registration? entry = null;
            if (csv.Parser.Count != headers.Length)
                error = "Column count does not match the header.";
            else
            {
                var first = Value("First Name");
                var last = Value("Last Name");
                var email = Value("Email")?.ToLowerInvariant();
                var mobile = Value("Mobile Number");
                var gender = Value("Gender");
                var emergency = Value("Emergency Contact");
                var date = Value("Birthdate");
                var raceMatches = races.Where(x => Same(x.Name, Value("Race"))
                    || (decimal.TryParse(Value("Race"), NumberStyles.Number, CultureInfo.InvariantCulture, out var km)
                        && x.DistanceKm == km)).ToArray();
                var birth = DateOnly.TryParseExact(date, ["yyyy-MM-dd", "M/d/yyyy"], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsedBirth) ? parsedBirth : (DateOnly?)null;

                if (first is null || last is null || first.Length > 100 || last.Length > 100)
                    error = "First and last names are required and limited to 100 characters each.";
                else if (email?.Length > 254 || (email is not null && !System.Net.Mail.MailAddress.TryCreate(email, out _))
                    || mobile?.Length > 40 || gender?.Length > 40 || emergency?.Length > 250)
                    error = "Check the format or length of the runner contact fields.";
                else if ((date is not null && birth is null) || birth > DateOnly.FromDateTime(DateTime.UtcNow))
                    error = "Use a valid birthdate in YYYY-MM-DD or M/D/YYYY format.";
                else if (raceMatches.Length != 1)
                    error = "Race must resolve to one active race in the event.";
                else if (!TryStatus(Value("Registration Status"), RegistrationStatus.Pending, out var status)
                    || !TryStatus(Value("Payment Status"), PaymentStatus.Unspecified, out var payment))
                    error = "Enter valid registration and payment statuses.";
                else
                {
                    var race = raceMatches[0];
                    var category = race.Categories.FirstOrDefault(x => Same(x.Name, Value("Category")));
                    var wave = race.StartWaves.FirstOrDefault(x => Same(x.Name, Value("Start Wave")));
                    var matches = runners.Where(x => Same(first, x.FirstName) && Same(last, x.LastName)
                        && (email is null || x.Email is null || Same(email, x.Email))).ToArray();
                    if (matches.Length > 1)
                        error = "Runner matches multiple profiles.";
                    else if ((category is null && Value("Category") is not null) || (wave is null && Value("Start Wave") is not null))
                        error = "Category or wave does not belong to this race.";
                    else
                    {
                        var runner = matches.SingleOrDefault() ?? new Runner { OrganizerId = item.OrganizerId,
                            FirstName = first, LastName = last, Email = email, Birthdate = birth, Gender = gender,
                            MobileNumber = mobile, EmergencyContact = emergency };
                        var identity = $"{first}\0{last}\0{email}";
                        var input = new RegistrationInput(race.Id, runner.Id, category?.Id, wave?.Id,
                            Value("Name on Bib"), Value("Bib Number"), Value("Team"), status, payment, options);
                        error = existing.Any(x => x.RunnerId == runner.Id) || !seen.Add(identity)
                            ? "Duplicate registration." : await Validate(item, runner, input, null, ct);
                        if (error is null)
                        {
                            entry = new Registration { EventId = item.Id, OrganizerId = item.OrganizerId, Runner = runner, RunnerId = runner.Id };
                            Apply(entry, input, input.BibNumber);
                        }
                    }
                }
            }
            rows.Add(new ImportRow(csv.Parser.RawRow, error, entry?.EffectiveBibDisplayName));
            if (entry is not null) entries.Add(entry);
        }
        var allocationError = BibAllocator.Allocate(options, existing.Select(x => x.BibNumber),
            entries.Select(x => x.BibNumber).ToArray(), out var bibs);
        if (allocationError is not null) return plan with { Error = allocationError };
        int assigned = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Error is not null) continue;
            entries[assigned].BibNumber = bibs[assigned];
            rows[i] = rows[i] with { BibNumber = bibs[assigned] };
            assigned++;
        }
        return plan;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool HeaderMatches(string key, string header) => Same(key, header.Trim()) || key switch
    {
        "Name on Bib" => new[] { "Bib Name", "Display Name", "Preferred Name" }.Any(x => Same(x, header.Trim())),
        "Race" => Same("Distance", header.Trim()) || Same("Race / Distance", header.Trim()),
        "Team" => Same("Club", header.Trim()) || Same("Team / Club", header.Trim()),
        _ => false
    };

    private static bool TryStatus<T>(string? value, T fallback, out T result) where T : struct, Enum
    {
        result = fallback;
        return value is null || (Enum.TryParse(value, true, out result) && Enum.IsDefined(result));
    }

    private static async Task<byte[]> ReadUpload(IFormFile file, CancellationToken ct)
    {
        if (file.Length is <= 0 or > MaxBytes) throw new InvalidDataException();
        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private string Actor() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider protection) =>
        protection.CreateProtector("RaceBib.Registration.Import.v1").ToTimeLimitedDataProtector();

    private string Fingerprint(byte[] bytes, string? mapping, string? assignment, IEnumerable<Registration> entries) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            file = Convert.ToHexString(SHA256.HashData(bytes)), mapping, assignment,
            assignments = entries.Select(x => new { x.RaceId, x.RaceCategoryId, x.StartWaveId, x.BibNumber,
                existingRunnerId = db.Entry(x.Runner).State == EntityState.Unchanged ? x.Runner.Id : (Guid?)null,
                x.Runner.FirstName, x.Runner.LastName, x.Runner.Birthdate })
        })));

    private static string ErrorCsv(IEnumerable<ImportRow> rows)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var csv = new CsvWriter(output, CultureInfo.InvariantCulture);
        csv.WriteField("Row");
        csv.WriteField("Error");
        csv.NextRecord();
        foreach (var row in rows.Where(x => x.Error is not null))
        {
            csv.WriteField(row.Row);
            csv.WriteField(row.Error);
            csv.NextRecord();
        }
        return output.ToString();
    }
}
