using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Api.Services;
using RaceTiming.Application;
using RaceTiming.Domain.Entities;

namespace RaceTiming.Api.Controllers;

public sealed partial class RegistrationsController
{
    private const int BulkBibScopeLimit = 1000;
    private const string BulkBibCollationConflict =
        "A proposed bib is already reserved under the event's bib rules. Choose a different range and preview again.";
    private static readonly TimeSpan BulkBibPreviewLifetime = TimeSpan.FromMinutes(10);

    public sealed record BulkBibInput(Guid? RaceId, int? Start, int? End, int Width);
    public sealed record BulkBibConfirmation(string? Token);

    private sealed record BulkBibRow(Guid RegistrationId, string RunnerName, string Race, string? CurrentBib,
        string? ProposedBib, string? SkipReason);

    private sealed record BulkBibPlan(List<Registration> Entries, BulkBibRow[] Rows, string? AllocationError,
        bool HasCollationConflict)
    {
        public string? Error => HasCollationConflict ? BulkBibCollationConflict : AllocationError;
    }
    private sealed record BulkBibTicket(Guid EventId, string Actor, BulkBibInput Input, string Hash);

    [HttpPost("bibs/preview")]
    [RequestSizeLimit(8192)]
    public Task<IActionResult> PreviewBulkBibs(Guid eventId, BulkBibInput input,
        [FromServices] IDataProtectionProvider protection, CancellationToken ct) =>
        WithEvent(eventId, false, ct, async item =>
        {
            if (!AllowsRegistrationWrites(item.Status)) return Conflict("Registration changes are closed for this event.");
            var (plan, scopeError) = await PrepareBulkBibs(item, input, false, ct);
            if (plan is null) return BadRequest(scopeError);

            var skipped = plan.Rows.Count(row => row.SkipReason is not null);
            var changeCount = plan.Rows.Count(row => row.ProposedBib is not null);
            var token = changeCount == 0 ? null : BulkBibProtector(protection).Protect(JsonSerializer.Serialize(
                new BulkBibTicket(eventId, Actor(), input, BulkBibHash(item, plan))), BulkBibPreviewLifetime);
            return Ok(new
            {
                scopeCount = plan.Rows.Length, eligibleCount = plan.Rows.Length - skipped, skippedCount = skipped,
                changeCount, plan.Rows, plan.Error, token
            });
        });

    [HttpPost("bibs/confirm")]
    [RequestSizeLimit(8192)]
    public Task<IActionResult> ConfirmBulkBibs(Guid eventId, BulkBibConfirmation input,
        [FromServices] IDataProtectionProvider protection, CancellationToken ct) =>
        WithEvent(eventId, true, ct, async item =>
        {
            if (input.Token is not { Length: > 0 and <= 4096 }) return StaleBulkBibPreview();
            BulkBibTicket? ticket;
            try
            {
                ticket = JsonSerializer.Deserialize<BulkBibTicket>(BulkBibProtector(protection).Unprotect(input.Token));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
            {
                return StaleBulkBibPreview();
            }
            if (ticket is null || ticket.EventId != eventId || ticket.Actor != Actor()) return StaleBulkBibPreview();

            var (plan, _) = await PrepareBulkBibs(item, ticket.Input, true, ct);
            if (plan?.HasCollationConflict == true) return Conflict(BulkBibCollationConflict);
            if (plan is null || plan.AllocationError is not null || ticket.Hash != BulkBibHash(item, plan))
                return StaleBulkBibPreview();

            var assignedCount = 0;
            var metadata = new Dictionary<string, object?> { ["eventId"] = eventId.ToString("D") };
            for (var i = 0; i < plan.Rows.Length; i++)
            {
                if (plan.Rows[i].ProposedBib is not { } bib) continue;
                var entry = plan.Entries[i];
                entry.BibNumber = bib;
                entry.Version++;
                Record(entry, AuditLogger.BulkBibAssigned, metadata);
                assignedCount++;
            }
            await db.SaveChangesAsync(ct);
            return Ok(new { assignedCount, skippedCount = plan.Rows.Length - assignedCount });
        });

    private async Task<(BulkBibPlan? Plan, string? ScopeError)> PrepareBulkBibs(
        Event item, BulkBibInput input, bool track, CancellationToken ct)
    {
        if (input.RaceId.HasValue && !await db.Races.AnyAsync(x => x.EventId == item.Id && x.Id == input.RaceId, ct))
            return (null, "Choose a race from this event.");

        var query = db.Registrations.Where(x => x.EventId == item.Id);
        if (input.RaceId.HasValue) query = query.Where(x => x.RaceId == input.RaceId);
        if (!track) query = query.AsNoTracking();
        var entries = await query.Include(x => x.Runner).Include(x => x.Race)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Take(BulkBibScopeLimit + 1).ToListAsync(ct);
        if (entries.Count > BulkBibScopeLimit)
            return (null, $"A preview supports at most {BulkBibScopeLimit:N0} registrations in scope. Choose a smaller race.");

        var rows = entries.Select(entry => new BulkBibRow(entry.Id,
            entry.Runner.FirstName + " " + entry.Runner.LastName, entry.Race.Name, entry.BibNumber, null,
            BulkBibEligibility.SkipReason(entry.RegistrationStatus, entry.Race.Status, entry.BibNumber, entry.CheckedInAtUtc.HasValue)))
            .ToArray();
        var eligibleCount = rows.Count(row => row.SkipReason is null);
        List<string?> occupied = eligibleCount == 0 ? [] : await db.Registrations.Where(x => x.EventId == item.Id && x.BibNumber != null)
            .Select(x => x.BibNumber).ToListAsync(ct);
        var error = BibAllocator.Allocate(new(BibAssignmentMode.Automatic, input.Start, input.End, input.Width),
            occupied, new string?[eligibleCount], out var assigned);
        var hasCollationConflict = false;
        if (error is null && eligibleCount > 0)
        {
            // Legacy values can compare equal only under the unique index's database collation.
            var proposed = assigned.OfType<string>().ToArray();
            hasCollationConflict = await db.Registrations.AnyAsync(x => x.EventId == item.Id && x.BibNumber != null &&
                proposed.Contains(x.BibNumber), ct);
        }
        if (error is null && !hasCollationConflict)
        {
            var next = 0;
            for (var i = 0; i < rows.Length; i++)
                if (rows[i].SkipReason is null) rows[i] = rows[i] with { ProposedBib = assigned[next++] };
        }
        return (new(entries, rows, error, hasCollationConflict), null);
    }

    private static string BulkBibHash(Event item, BulkBibPlan plan) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            item.Status,
            registrations = plan.Entries.Select(x => new { x.Id, x.Version, x.RaceId, raceStatus = x.Race.Status }),
            plan.Rows
        })));

    private static ITimeLimitedDataProtector BulkBibProtector(IDataProtectionProvider protection) =>
        protection.CreateProtector("RaceBib.Registration.BulkBib.v1").ToTimeLimitedDataProtector();

    private ConflictObjectResult StaleBulkBibPreview() =>
        Conflict("This bulk bib preview is stale, expired, or already applied. Preview again before confirming.");
}
