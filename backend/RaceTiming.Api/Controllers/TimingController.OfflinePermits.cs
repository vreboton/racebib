using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Api.Services;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;

namespace RaceTiming.Api.Controllers;

public sealed partial class TimingController
{
    public sealed record OfflinePolicyInput(int Hours, string Reason, Guid Generation);

    [HttpPut("checkpoints/{checkpointId:guid}/assignments/{userId}/offline-policy")]
    public Task<IActionResult> SetOfflinePolicy(Guid eventId, Guid checkpointId, string userId,
        OfflinePolicyInput input, CancellationToken ct) =>
        Write(eventId, ct, async item =>
        {
            if (input.Hours is < 12 or > 48 || !ValidText(input.Reason, 300))
                return BadRequest("Provide an authorization duration from 12 to 48 hours and a reason.");
            if (item.Status == EventStatus.Completed)
                return ConflictProblem("event_completed", "Offline authorization cannot change after completion.");

            var grant = await db.Set<CheckpointAssignment>().FindAsync([eventId, checkpointId, userId], ct);
            if (grant is null || !grant.Active) return NotFound();
            if (grant.Generation != input.Generation)
                return ConflictProblem("offline_assignment_changed", "Reload the assignment before approving an offline permit limit.");
            var previousHours = grant.OfflineHours;
            grant.OfflineHours = input.Hours;
            grant.OfflinePolicyRevision = Guid.NewGuid();
            audit.Record(User, "Timing.OfflinePolicyChanged", nameof(CheckpointAssignment), grant.Generation.ToString(),
                item.OrganizerId, new Dictionary<string, object?> { ["eventId"] = eventId.ToString(), ["checkpointId"] = checkpointId.ToString(), ["operatorId"] = userId,
                    ["policyRevision"] = grant.OfflinePolicyRevision.ToString(), ["previousHours"] = previousHours.ToString(),
                    ["hours"] = input.Hours.ToString(), ["reason"] = input.Reason.Trim() });
            return NoContent();
        });

    [HttpPost("checkpoints/{checkpointId:guid}/offline-permit")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> IssueOfflinePermit(Guid eventId, Guid checkpointId,
        [FromServices] StationPermitSigner signer, CancellationToken ct) =>
        Write(eventId, ct, async item =>
        {
            var grant = await Grants.SingleOrDefaultAsync(x => x.EventId == eventId && x.CheckpointId == checkpointId, ct);
            if (grant is null) return Forbid();
            var now = DateTimeOffset.UtcNow;
            if (item.Status != EventStatus.Live || now < Utc(item.StartDate) || now >= Utc(item.EndDate))
                return ConflictProblem("offline_window_closed", "Offline capture is outside the live event window.");

            var frozenEnd = await (from setup in db.Set<RaceTimingSetup>()
                join visit in db.Set<TimingVisit>() on setup.RaceId equals visit.RaceId
                where setup.EventId == eventId && visit.EventId == eventId && visit.CheckpointId == checkpointId &&
                    setup.WindowStartUtc <= now.UtcDateTime && setup.WindowEndUtc > now.UtcDateTime
                select (DateTime?)setup.WindowEndUtc).MaxAsync(ct);
            if (frozenEnd is null)
                return ConflictProblem("offline_window_closed", "This station has no active frozen timing window.");

            var expiry = new[] { now.AddHours(grant.OfflineHours), Utc(item.EndDate), Utc(frozenEnd.Value) }.Min();
            var claims = new StationPermitClaims(Guid.NewGuid(), Actor, eventId, checkpointId, grant.Generation,
                grant.OfflinePolicyRevision, now.ToUnixTimeSeconds(), expiry.ToUnixTimeSeconds());
            if (claims.ExpiresAt <= claims.IssuedAt)
                return ConflictProblem("offline_window_closed", "This station's timing window is ending.");
            var permit = signer.Sign(claims);
            if (permit is null) return StatusCode(503, new ProblemDetails { Title = "Offline permit signing is unavailable." });

            audit.Record(User, "Timing.OfflinePermitIssued", nameof(CheckpointAssignment), claims.Id.ToString(),
                item.OrganizerId, new Dictionary<string, object?> { ["eventId"] = eventId.ToString(), ["checkpointId"] = checkpointId.ToString(), ["operatorId"] = Actor,
                    ["generation"] = grant.Generation.ToString(), ["policyRevision"] = grant.OfflinePolicyRevision.ToString(),
                    ["issuedAt"] = claims.IssuedAt.ToString(), ["expiresAt"] = claims.ExpiresAt.ToString() });
            return Ok(permit);
        }, manager: false);
}
