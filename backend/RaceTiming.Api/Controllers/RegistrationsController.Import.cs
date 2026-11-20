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
        "Race", "Category", "Start Wave", "Bib Number", "Team", "Emergency Contact", "Registration Status", "Payment Status", "Runner ID"
    ];
    private sealed record ImportRow(int Row, string? Error, string? NameOnBib = null, string? BibNumber = null);
    private static readonly string[] RequiredColumns = ["First Name", "Last Name", "Race"];
    private sealed record ImportPlan(List<ImportRow> Rows, List<Registration> Entries, string? Error = null);
    private sealed record ImportTicket(Guid Id, Guid EventId, string Actor, string Hash);

    [HttpPost("import/preview")]
    [RequestSizeLimit(2 * MaxBytes)]
    public Task<IActionResult> Preview(Guid eventId, IFormFile file,
        [FromForm] string? assignment, [FromServices] IDataProtectionProvider protection, CancellationToken ct) =>
        WithEvent(eventId, false, ct, async item =>
        {
            try
            {
                var bytes = await ReadUpload(file, ct);
                var plan = await Prepare(item, bytes, assignment, ct);
                var valid = plan.Error is null && plan.Entries.Count > 0 && plan.Rows.All(x => x.Error is null);
                var token = valid ? Protector(protection).Protect(JsonSerializer.Serialize(
                    new ImportTicket(Guid.NewGuid(), eventId, Actor(), Fingerprint(bytes, assignment,
                        plan.Entries))), TimeSpan.FromMinutes(20)) : null;
                return Ok(new { plan.Rows, valid, token,
                    plan.Error, validCount = plan.Rows.Count(x => x.Error is null),
                    errorCsv = ErrorCsv(plan.Rows) });
            }
            catch (Exception ex) when (ex is CsvHelperException or DecoderFallbackException or JsonException or InvalidDataException)
            {
                return BadRequest("Download the RaceBib CSV template and upload a well-formed UTF-8 CSV within the 1 MiB and 1,000 row limits. Check the bib assignment settings.");
            }
        });

    [HttpPost("import/confirm")]
    [RequestSizeLimit(2 * MaxBytes)]
    public Task<IActionResult> Confirm(Guid eventId, IFormFile file, [FromForm] string token,
        [FromForm] string? assignment, [FromServices] IDataProtectionProvider protection,
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
            await RunnerProfileWriteLock.Acquire(db, item.OrganizerId, ct);
            var plan = await Prepare(item, bytes, assignment, ct);
            if (plan.Error is not null || plan.Entries.Count == 0 || plan.Rows.Any(x => x.Error is not null)
                || ticket.Hash != Fingerprint(bytes, assignment, plan.Entries))
                return Conflict("The file, bib assignment or event data changed. Preview again.");

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

    private async Task<ImportPlan> Prepare(Event item, byte[] bytes, string? assignmentJson, CancellationToken ct)
    {
        var source = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        using var csv = new CsvReader(new StringReader(source), CultureInfo.InvariantCulture);
        if (!csv.Read())
            return new([], [], "The CSV has no header row. Download the RaceBib CSV template and keep its header row.");
        csv.ReadHeader();
        var headers = csv.HeaderRecord!;
        var columnIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < headers.Length; i++)
        {
            var header = headers[i];
            if (!Columns.Contains(header, StringComparer.Ordinal))
                return new([], [], $"Unrecognized header in column {i + 1}. Download the RaceBib CSV template and use its exact column names.");
            if (!columnIndexes.TryAdd(header, i))
                return new([], [], $"Duplicate header '{header}'. Keep only one copy of each column in the RaceBib CSV template.");
        }

        var missing = RequiredColumns.Where(column => !columnIndexes.ContainsKey(column)).ToArray();
        if (missing.Length > 0)
            return new([], [], $"Missing required headers: {string.Join(", ", missing)}. Download the RaceBib CSV template.");

        var options = assignmentJson is null ? new BibAssignment()
            : JsonSerializer.Deserialize<BibAssignment>(assignmentJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException();
        var rows = new List<ImportRow>();
        var entries = new List<Registration>();
        var plan = new ImportPlan(rows, entries);

        var races = await RacesWithRegistrationOptions(item.Id)
            .Where(x => x.Status == RecordStatus.Active).ToListAsync(ct);
        var existing = await db.Registrations.Where(x => x.EventId == item.Id)
            .Select(x => new { x.RunnerId, x.BibNumber }).ToListAsync(ct);
        var runners = await db.Runners.Where(x => x.OrganizerId == item.OrganizerId).ToListAsync(ct);
        var registered = existing.Select(x => x.RunnerId).ToHashSet();
        var seen = new HashSet<Guid>();
        var newNames = new HashSet<(string First, string Last)>();
        var newEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (csv.Read())
        {
            if (rows.Count >= MaxRows) throw new InvalidDataException();
            string? Value(string key) => columnIndexes.TryGetValue(key, out var index) ? Optional(csv.GetField(index)) : null;
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
                else if (email?.Length > 254 || (email is not null && (!System.Net.Mail.MailAddress.TryCreate(email, out var address) || !Same(email, address.Address)))
                    || mobile?.Length > 40 || gender?.Length > 40 || emergency?.Length > 250)
                    error = "Check the format or length of the runner contact fields.";
                else if ((date is not null && birth is null) || birth > DateOnly.FromDateTime(DateTime.UtcNow))
                    error = "Use a valid birthdate in YYYY-MM-DD or M/D/YYYY format.";
                else if (Value("Race") is null)
                    error = "Race is required.";
                else if (raceMatches.Length != 1)
                    error = "The Race column must match one active Race Category in the event.";
                else if (!TryStatus(Value("Registration Status"), RegistrationStatus.Pending, out var status)
                    || !TryStatus(Value("Payment Status"), PaymentStatus.Unspecified, out var payment))
                    error = "Enter valid registration and payment statuses.";
                else
                {
                    var race = raceMatches[0];
                    var category = race.Categories.FirstOrDefault(x => Same(x.Name, Value("Category")));
                    var wave = race.StartWaves.FirstOrDefault(x => Same(x.Name, Value("Start Wave")));
                    var (profile, identityError) = ResolveProfile(runners, Value("Runner ID"), first, last, email);
                    if (identityError is not null)
                        error = identityError;
                    else if ((category is null && Value("Category") is not null) || (wave is null && Value("Start Wave") is not null))
                        error = "The Age Group or Start Wave does not belong to this Race Category.";
                    else if (profile is not null && birth.HasValue && profile.Birthdate != birth)
                        error = profile.Birthdate.HasValue
                            ? "CSV birthdate differs from the runner profile. Verify both sources, then correct the CSV or update the profile before importing."
                            : "The runner profile has no verified birthdate. Verify and save the birthdate on the profile before importing it.";
                    else if (profile is not null && !profile.Birthdate.HasValue &&
                        (category?.MinimumAge.HasValue == true || category?.MaximumAge.HasValue == true))
                        error = "This Age Group requires a birthdate on the runner profile. Verify and save it, or choose an Age Group without age limits.";
                    else
                    {
                        var runner = profile ?? new Runner { OrganizerId = item.OrganizerId,
                            FirstName = first, LastName = last, Email = email, Birthdate = birth, Gender = gender,
                            MobileNumber = mobile, EmergencyContact = emergency };
                        var input = new RegistrationInput(race.Id, runner.Id, category?.Id, wave?.Id,
                            Value("Name on Bib"), Value("Bib Number"), Value("Team"), status, payment, options);
                        var newName = (first.ToUpperInvariant(), last.ToUpperInvariant());
                        if (registered.Contains(runner.Id) || !seen.Add(runner.Id))
                            error = "Duplicate registration. This runner is already registered for this event or appears earlier in the file.";
                        else if (profile is null && (!newNames.Add(newName) || (email is not null && !newEmails.Add(email))))
                            error = "Duplicate or ambiguous new runner in the CSV. Verify the rows and use distinct verified Runner IDs for different people.";
                        else
                            error = Validate(item, race, runner, input, null);

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
        if (rows.Count == 0) return plan with { Error = "Add at least one registration below the template header." };
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

    private static (Runner? Profile, string? Error) ResolveProfile(
        IReadOnlyList<Runner> profiles, string? runnerId, string first, string last, string? email)
    {
        bool SameName(Runner profile) => Same(profile.FirstName, first) && Same(profile.LastName, last);

        if (runnerId is not null)
        {
            var selected = Guid.TryParse(runnerId, out var id) ? profiles.FirstOrDefault(x => x.Id == id) : null;
            if (selected is null)
                return (null, "Runner ID does not identify a profile in this organizer. Copy the ID from the verified runner profile.");
            if (!SameName(selected) || (email is not null && !Same(selected.Email, email)))
                return (null, "Name or email conflicts with the selected Runner ID. Verify the profile and correct the CSV.");
            return (selected, null);
        }

        if (email is not null)
        {
            var matches = profiles.Where(x => Same(x.Email, email)).Take(2).ToArray();
            if (matches.Length == 1 && SameName(matches[0]))
                return (matches[0], null);
            if (matches.Length > 0)
                return (null, "Email matches an ambiguous or differently named profile. Verify the runner and use their Runner ID.");
        }

        if (profiles.Any(SameName))
            return (null, "An existing profile has this name, but the email is missing or does not match. Verify the runner and use their Runner ID, or create a separate profile for a different person.");

        return (null, null);
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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
        protection.CreateProtector("RaceBib.Registration.Import.v3").ToTimeLimitedDataProtector();

    private string Fingerprint(byte[] bytes, string? assignment, IEnumerable<Registration> entries) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            file = Convert.ToHexString(SHA256.HashData(bytes)), assignment,
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
