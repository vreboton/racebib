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
        "Race", "Category", "Start Wave", "Bib Number", "Team", "Emergency Contact"
    ];
    private sealed record ImportRow(int Row, string? Error, string? NameOnBib = null, string? BibNumber = null,
        string? DefaultRaceReason = null, string? CsvRace = null, string? RaceName = null, string? Warning = null);
    private static readonly string[] RequiredColumns = ["First Name", "Last Name", "Email"];
    private sealed record ImportPlan(List<ImportRow> Rows, List<Registration> Entries, string? Error = null)
    {
        public HashSet<Guid> RacesWithoutCategories { get; } = [];
    }
    private sealed record ImportTicket(Guid Id, Guid EventId, string Actor, string Hash);

    [HttpPost("import/preview")]
    [RequestSizeLimit(2 * MaxBytes)]
    public Task<IActionResult> Preview(Guid eventId, IFormFile file,
        [FromForm] Guid defaultRaceId, [FromForm] string? assignment, [FromServices] IDataProtectionProvider protection, CancellationToken ct) =>
        WithEvent(eventId, false, ct, async item =>
        {
            try
            {
                var bytes = await ReadUpload(file, ct);
                var plan = await Prepare(item, defaultRaceId, bytes, assignment, ct);
                var valid = plan.Error is null && plan.Entries.Count > 0 && plan.Rows.All(x => x.Error is null);
                var token = valid ? Protector(protection).Protect(JsonSerializer.Serialize(
                    new ImportTicket(Guid.NewGuid(), eventId, Actor(), Fingerprint(defaultRaceId, bytes, assignment,
                        plan.Entries))), TimeSpan.FromMinutes(20)) : null;
                return Ok(new { plan.Rows, valid, token,
                    plan.Error, validCount = plan.Rows.Count(x => x.Error is null),
                    defaultRaceCount = CountDefaultRaceRows(plan.Rows), errorCsv = ErrorCsv(plan.Rows) });
            }
            catch (Exception ex) when (ex is CsvHelperException or DecoderFallbackException or JsonException or InvalidDataException)
            {
                return BadRequest("Download the RaceBib CSV template and upload a well-formed UTF-8 CSV within the 1 MiB and 1,000 row limits. Check the bib assignment settings.");
            }
        });

    [HttpPost("import/confirm")]
    [RequestSizeLimit(2 * MaxBytes)]
    public Task<IActionResult> Confirm(Guid eventId, IFormFile file, [FromForm] string token,
        [FromForm] Guid defaultRaceId, [FromForm] string? assignment, [FromServices] IDataProtectionProvider protection,
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
            var plan = await Prepare(item, defaultRaceId, bytes, assignment, ct);
            if (plan.Error is not null || plan.Entries.Count == 0 || plan.Rows.Any(x => x.Error is not null)
                || ticket.Hash != Fingerprint(defaultRaceId, bytes, assignment, plan.Entries))
                return Conflict("The file, default race, bib assignment, runner profile or event data changed. Preview again.");

            // The preview uses the unresolved assignment. Provision only after validating the preview under the event lock.
            var newCategories = plan.RacesWithoutCategories.ToDictionary(id => id, id => AddRegistrationCategory(id).Id);
            db.Registrations.AddRange(plan.Entries);
            foreach (var entry in plan.Entries)
            {
                if (newCategories.TryGetValue(entry.RaceId, out var categoryId)) entry.RaceCategoryId = categoryId;
                Record(entry, "Registration.Imported");
                if (entry.BibNumber is not null) Record(entry, "Registration.BibChanged");
            }
            audit.Record(User, "Registration.ImportCompleted", "RegistrationImport", receipt, item.OrganizerId);
            await db.SaveChangesAsync(ct);
            return Ok(new { importedCount = plan.Entries.Count, defaultRaceCount = CountDefaultRaceRows(plan.Rows) });
        }
        catch (Exception ex) when (ex is CsvHelperException or DecoderFallbackException
            or JsonException or InvalidDataException or CryptographicException)
        {
            return Conflict("The import preview is invalid or expired. Preview again.");
        }
    });

    private async Task<ImportPlan> Prepare(Event item, Guid defaultRaceId, byte[] bytes, string? assignmentJson, CancellationToken ct)
    {
        var races = await RacesWithRegistrationOptions(item.Id).ToListAsync(ct);
        var defaultRace = races.SingleOrDefault(race => race.Id == defaultRaceId && race.Status == RecordStatus.Active);
        if (defaultRace is null)
            return new([], [], "Select an active default race from this event.");

        var racesByName = races.ToLookup(race => race.Name.Trim(), StringComparer.OrdinalIgnoreCase);
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

        var existing = await db.Registrations.Where(x => x.EventId == item.Id)
            .Select(x => new { x.RunnerId, x.BibNumber }).ToListAsync(ct);
        var runners = await db.Runners.Where(x => x.OrganizerId == item.OrganizerId && x.Email != null).ToListAsync(ct);
        var profilesByEmail = runners.ToLookup(x => NormalizeImportEmail(x.Email)!, StringComparer.Ordinal);
        var registered = existing.Select(x => x.RunnerId).ToHashSet();
        var seenEmails = new HashSet<string>(StringComparer.Ordinal);
        while (csv.Read())
        {
            if (rows.Count >= MaxRows) throw new InvalidDataException();
            string? Value(string key) => columnIndexes.TryGetValue(key, out var index) ? Optional(csv.GetField(index)) : null;
            string? error;
            Registration? entry = null;
            string? csvRace = null;
            string? defaultRaceReason = null;
            string? raceName = null;
            string? warning = null;
            if (csv.Parser.Count != headers.Length)
                error = "Column count does not match the header.";
            else
            {
                var first = Value("First Name");
                var last = Value("Last Name");
                var email = NormalizeImportEmail(Value("Email"));
                var mobile = Value("Mobile Number");
                var gender = Value("Gender");
                var emergency = Value("Emergency Contact");
                var date = Value("Birthdate");
                var birth = DateOnly.TryParseExact(date, ["yyyy-MM-dd", "M/d/yyyy"], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsedBirth) ? parsedBirth : (DateOnly?)null;

                if (first is null || last is null || first.Length > 100 || last.Length > 100)
                    error = "First and last names are required and limited to 100 characters each.";
                else if (email is null)
                    error = "Email is required.";
                else if (email.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(email, out var address) || !Same(email, address.Address) || !address.Host.Contains('.'))
                    error = "Enter a valid email address.";
                else if (mobile?.Length > 40 || gender?.Length > 40 || emergency?.Length > 250)
                    error = "Check the format or length of the runner contact fields.";
                else if ((date is not null && birth is null) || birth > DateOnly.FromDateTime(DateTime.UtcNow))
                    error = "Use a valid birthdate in YYYY-MM-DD or M/D/YYYY format.";
                else if (!seenEmails.Add(email))
                    error = "Duplicate email in the CSV. Keep one row per runner.";
                else
                {
                    csvRace = Value("Race");
                    var matches = csvRace is null ? [] : racesByName[csvRace].Take(2).ToArray();
                    var race = matches.Length == 1 ? matches[0] : defaultRace;
                    defaultRaceReason = matches.Length == 0 ? (csvRace is null ? "blank" : "unrecognized") : null;
                    raceName = matches.Length <= 1 ? race.Name : null;
                    var categoryName = Value("Category");
                    var requestedCategory = race.Categories.FirstOrDefault(x => Same(x.Name, categoryName));
                    var categoryError = ResolveCategory(race, requestedCategory?.Id, null, out var categoryId);
                    var category = race.Categories.FirstOrDefault(x => x.Id == categoryId);
                    var wave = race.StartWaves.FirstOrDefault(x => Same(x.Name, Value("Start Wave")));
                    var profiles = profilesByEmail[email].Take(2).ToArray();
                    var profile = profiles.Length == 1 ? profiles[0] : null;
                    if (profile is not null && (first != profile.FirstName || last != profile.LastName))
                        warning = $"CSV name {first} {last} differs from the saved profile name. This import uses the saved profile for {profile.FirstName} {profile.LastName}.";
                    if (matches.Length > 1)
                        error = "The Race column matches multiple races in this event. Use an unambiguous race name.";
                    else if (race.Status != RecordStatus.Active)
                        error = "The CSV race is inactive. Choose an active race.";
                    else if (profiles.Length > 1)
                        error = "This email matches multiple runner profiles in this organizer. Resolve the duplicate profiles before importing.";
                    else if ((requestedCategory is null && categoryName is not null) || (wave is null && Value("Start Wave") is not null))
                        error = "Registration category or Start Wave does not belong to this race.";
                    else if (profile is not null && !profile.Birthdate.HasValue &&
                        (category?.MinimumAge.HasValue == true || category?.MaximumAge.HasValue == true))
                        error = "This Age Group requires a birthdate on the runner profile. Verify and save it, or choose an Age Group without age limits.";
                    else
                    {
                        var runner = profile ?? new Runner { OrganizerId = item.OrganizerId,
                            FirstName = first, LastName = last, Email = email, Birthdate = birth, Gender = gender,
                            MobileNumber = mobile, EmergencyContact = emergency };
                        var input = new RegistrationInput(race.Id, runner.Id, categoryId, wave?.Id,
                            Value("Name on Bib") ?? $"{first} {last}", Value("Bib Number"), Value("Team"),
                            RegistrationStatus.Confirmed, PaymentStatus.Paid, options);
                        if (registered.Contains(runner.Id))
                            error = "Duplicate registration. This runner is already registered for this event.";
                        else
                            error = categoryError ?? Validate(item, race, runner, input, null);

                        if (error is null)
                        {
                            entry = new Registration { EventId = item.Id, OrganizerId = item.OrganizerId, Runner = runner, RunnerId = runner.Id };
                            Apply(entry, input, input.BibNumber);
                            if (race.Categories.Count == 0) plan.RacesWithoutCategories.Add(race.Id);
                        }
                    }
                }
            }
            rows.Add(new ImportRow(csv.Parser.RawRow, error, entry?.EffectiveBibDisplayName,
                DefaultRaceReason: entry is null ? null : defaultRaceReason, CsvRace: csvRace, RaceName: raceName, Warning: warning));
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

    private static int CountDefaultRaceRows(IEnumerable<ImportRow> rows) =>
        rows.Count(row => row.Error is null && row.DefaultRaceReason is not null);

    private static string? NormalizeImportEmail(string? email) => Optional(email)?.ToLowerInvariant();

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadUpload(IFormFile file, CancellationToken ct)
    {
        if (file.Length is <= 0 or > MaxBytes) throw new InvalidDataException();
        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private string Actor() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider protection) =>
        protection.CreateProtector("RaceBib.Registration.Import.v6").ToTimeLimitedDataProtector();

    private string Fingerprint(Guid defaultRaceId, byte[] bytes, string? assignment, IEnumerable<Registration> entries) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            file = Convert.ToHexString(SHA256.HashData(bytes)), defaultRaceId, assignment,
            assignments = entries.Select(x => new { x.RaceId, x.RaceCategoryId, x.StartWaveId, x.BibNumber, x.BibDisplayName,
                existingRunnerId = db.Entry(x.Runner).State == EntityState.Unchanged ? x.Runner.Id : (Guid?)null,
                normalizedEmail = NormalizeImportEmail(x.Runner.Email), x.Runner.FirstName, x.Runner.LastName, x.Runner.Birthdate })
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
