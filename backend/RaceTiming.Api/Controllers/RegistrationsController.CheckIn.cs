using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Api.Services;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Domain.Services;

namespace RaceTiming.Api.Controllers;

public sealed partial class RegistrationsController
{
    public sealed record ReverseCheckInInput(long? Version, string? Reason);

    [HttpGet("check-in")]
    public Task<IActionResult> SearchCheckIn(Guid eventId, string? search, int page = 1, CancellationToken ct = default) =>
        WithEvent(eventId, false, ct, async item =>
        {
            const int pageSize = 30;
            var term = search?.Trim();
            var query = db.Registrations.AsNoTracking().Where(x => x.EventId == eventId);
            if (!string.IsNullOrEmpty(term))
                query = query.Where(x => x.Runner.FirstName.Contains(term) || x.Runner.LastName.Contains(term) ||
                    (x.Runner.FirstName + " " + x.Runner.LastName).Contains(term) ||
                    (x.BibNumber != null && x.BibNumber.Contains(term)));

            var total = await query.CountAsync(ct);
            var entries = await query.Include(x => x.Runner).Include(x => x.Race)
                .OrderByDescending(x => term != null && x.BibNumber == term)
                .ThenBy(x => x.Runner.LastName).ThenBy(x => x.Runner.FirstName).ThenBy(x => x.Id)
                .Skip((Math.Clamp(page, 1, 100_000) - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            return Ok(new { total, pageSize, items = entries.Select(x => Verification(x, item.Status)) });
        });

    [HttpPut("{id:guid}/check-in")]
    public Task<IActionResult> CheckIn(Guid eventId, Guid id, VersionInput input, CancellationToken ct) =>
        SetCheckIn(eventId, id, input.Version, false, null, ct);

    [HttpDelete("{id:guid}/check-in")]
    public Task<IActionResult> ReverseCheckIn(Guid eventId, Guid id, ReverseCheckInInput input, CancellationToken ct) =>
        SetCheckIn(eventId, id, input.Version, true, input.Reason?.Trim(), ct);

    private Task<IActionResult> SetCheckIn(Guid eventId, Guid id, long? version, bool reverse, string? reason,
        CancellationToken ct) => WithEvent(eventId, true, ct, async item =>
    {
        var entry = await db.Registrations.Include(x => x.Runner).Include(x => x.Race)
            .SingleOrDefaultAsync(x => x.EventId == eventId && x.Id == id, ct);
        if (entry is null) return NotFound();
        if (entry.Version != version) return StaleRegistration();
        if (reverse && !AuditLogger.IsCheckInReversalReason(reason))
            return BadRequest($"Enter a reversal reason of 1 to {AuditLogger.MaxReversalReasonLength} characters on one line.");

        if (entry.CheckedInAtUtc.HasValue != reverse) return Ok(Verification(entry, item.Status));

        if (!reverse && RegistrationCheckIn.GetIneligibility(item.Status, entry.RegistrationStatus, entry.Race.Status, entry.BibNumber) is { } error)
            return Conflict(error);

        // Match MySQL datetime(6) precision so every response returns the same timestamp.
        var now = DateTime.UtcNow;
        entry.CheckedInAtUtc = reverse ? null : now.AddTicks(-(now.Ticks % 10));
        entry.Version++;
        Record(entry, reverse ? AuditLogger.CheckInReversed : "Registration.CheckedIn",
            reverse ? new Dictionary<string, object?> { ["reason"] = reason } : null);
        await db.SaveChangesAsync(ct);
        return Ok(Verification(entry, item.Status));
    });

    private static ConflictObjectResult ReverseCheckInFirst() =>
        new("Reverse check-in before changing the runner's race, category, wave, bib, or registration status.");

    private static DateTime? CheckInTimestamp(Registration entry) => entry.CheckedInAtUtc is { } timestamp
        ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc) : null;

    private static object Verification(Registration entry, EventStatus status) => new
    {
        entry.Id, entry.Version, entry.Runner.FirstName, entry.Runner.LastName, entry.BibNumber,
        race = entry.Race.Name, status = (int)entry.RegistrationStatus, checkedInAtUtc = CheckInTimestamp(entry),
        checkInError = RegistrationCheckIn.GetIneligibility(status, entry.RegistrationStatus, entry.Race.Status, entry.BibNumber),
        canReverse = AllowsRegistrationWrites(status)
    };
}
