using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Application;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;

namespace RaceTiming.Api.Controllers;

public sealed partial class TimingController
{
    public sealed record ObservationInput(Guid ObservationId, Guid RegistrationId, Guid RaceId, string BibNumber,
        Guid? OccurrenceId, DateTimeOffset ObservedAtUtc, Guid DeviceId, Guid AssignmentGeneration, TimingSource Source);
    public sealed record ConfirmInput(Guid OccurrenceId, Guid AssignmentGeneration, int ExpectedRevision);
    public sealed record ResolveInput(Guid? OccurrenceId, int ExpectedRevision, string Reason);

    [HttpGet("checkpoints/{checkpointId:guid}/registrations")]
    public Task<IActionResult> ResolveBib(Guid eventId, Guid checkpointId, string bib, CancellationToken ct) =>
        Write(eventId, ct, async _ =>
        {
            if (!await Grants.AnyAsync(x => x.EventId == eventId && x.CheckpointId == checkpointId, ct)) return Forbid();
            if (BibAllocator.Allocate(new(), [], [bib], out var normalized) is not null || normalized[0] is not string number)
                return BadRequest("Enter a valid bib number.");
            var runner = await (from roster in db.Set<TimingRosterEntry>()
                join registration in db.Registrations on roster.RegistrationId equals registration.Id
                where roster.EventId == eventId && roster.BibNumber == number &&
                    db.Set<TimingVisit>().Any(x => x.RaceId == roster.RaceId && x.CheckpointId == checkpointId)
                select new { roster.RegistrationId, roster.RaceId, roster.BibNumber, registration.BibDisplayName, registration.Runner.FirstName, registration.Runner.LastName })
                .SingleOrDefaultAsync(ct);
            if (runner is null) return NotFound();
            if (await HasConflictingLiveBibOwner(eventId, runner.RegistrationId, runner.BibNumber, ct))
                return BibIdentityConflict();
            var visits = await Visits(runner.RaceId, checkpointId).ToListAsync(ct);
            return Ok(new { runner.RegistrationId, runner.RaceId, runner.BibNumber, displayName = Registration.DisplayName(runner.BibDisplayName, runner.FirstName, runner.LastName),
                occurrences = visits.Select(VisitResponse) });
        }, manager: false);

    [HttpPost("checkpoints/{checkpointId:guid}/observations")]
    public Task<IActionResult> Submit(Guid eventId, Guid checkpointId, [FromHeader(Name = "Idempotency-Key")] Guid key,
        ObservationInput input, CancellationToken ct)
    {
        var received = Instant(DateTimeOffset.UtcNow);
        return Write(eventId, ct, async item =>
        {
            if (!await HasGrant(eventId, checkpointId, input.AssignmentGeneration, ct)) return Forbid();
            if (key == Guid.Empty || input.ObservationId == Guid.Empty || input.DeviceId == Guid.Empty ||
                input.ObservedAtUtc.UtcDateTime.Year < 1000 || input.ObservedAtUtc.UtcTicks % 10 != 0 || !Enum.IsDefined(input.Source) ||
                BibAllocator.Allocate(new(), [], [input.BibNumber], out var bibs) is not null || bibs[0] is not string bib)
                return BadRequest("Provide valid submission identifiers, bib, source, and observed time with at most microsecond precision.");
            var observed = Instant(input.ObservedAtUtc);
            var previous = await db.Set<TimingObservation>().SingleOrDefaultAsync(x =>
                x.EventId == eventId && x.ActorUserId == Actor && x.DeviceId == input.DeviceId && x.IdempotencyKey == key, ct);
            if (previous is not null)
            {
                if (previous.Id != input.ObservationId || previous.RegistrationId != input.RegistrationId || previous.RaceId != input.RaceId ||
                    previous.CheckpointId != checkpointId || previous.ProposedOccurrenceId != input.OccurrenceId ||
                    previous.ObservedAtUtc != observed || previous.Source != input.Source ||
                    previous.AssignmentGeneration != input.AssignmentGeneration || !string.Equals(previous.BibNumber, bib, StringComparison.OrdinalIgnoreCase))
                    return ConflictProblem("idempotency_mismatch", "This key was used for a different submission.");
                var initial = await db.Set<TimingDecision>().SingleAsync(x => x.ObservationId == previous.Id && x.Revision == 1, ct);
                return Ok(Receipt(previous, initial));
            }
            if (item.Status != EventStatus.Live) return ConflictProblem("capture_closed", "The event is closed for timing entry.");
            if (await db.Set<TimingObservation>().AnyAsync(x => x.Id == input.ObservationId, ct))
                return ConflictProblem("observation_id_used", "This observation identifier is already in use.");
            var roster = await db.Set<TimingRosterEntry>().SingleOrDefaultAsync(x =>
                x.EventId == eventId && x.BibNumber == bib, ct);
            if (roster is not null && await HasConflictingLiveBibOwner(eventId, roster.RegistrationId, roster.BibNumber, ct))
                return BibIdentityConflict();
            if (roster is null || roster.RegistrationId != input.RegistrationId || roster.RaceId != input.RaceId ||
                !string.Equals(roster.BibNumber, bib, StringComparison.OrdinalIgnoreCase))
                return ConflictProblem("roster_mismatch", "Resolve the bib against the armed timing roster again.");
            var visits = await Visits(input.RaceId, checkpointId).ToListAsync(ct);
            if (visits.Count == 0 || input.OccurrenceId.HasValue && visits.All(x => x.OccurrenceId != input.OccurrenceId))
                return NotFound();
            var observation = new TimingObservation
            {
                Id = input.ObservationId, EventId = eventId, CheckpointId = checkpointId, RaceId = input.RaceId,
                RegistrationId = roster.RegistrationId, BibNumber = roster.BibNumber, ProposedOccurrenceId = input.OccurrenceId,
                ObservedAtUtc = observed, ReceivedAtUtc = received, ActorUserId = Actor, DeviceId = input.DeviceId,
                IdempotencyKey = key, AssignmentGeneration = input.AssignmentGeneration, Source = input.Source
            };
            var selected = input.OccurrenceId ?? (visits.Count == 1 ? visits[0].OccurrenceId : null);
            var setup = await db.Set<RaceTimingSetup>().SingleAsync(x => x.RaceId == input.RaceId, ct);
            var outcome = observed < setup.WindowStartUtc || observed > setup.WindowEndUtc || observed > received.AddMinutes(2)
                ? TimingOutcome.SuspiciousClock
                : selected is Guid visitId ? await ValidateSlot(observation, visitId, ct) : TimingOutcome.AmbiguousOccurrence;
            db.Add(observation);
            var decision = AddDecision(observation, 1, selected, outcome, outcome.ToString());
            return StatusCode(201, Receipt(observation, decision));
        }, manager: false);
    }

    [HttpPost("checkpoints/{checkpointId:guid}/observations/{observationId:guid}/confirm")]
    public Task<IActionResult> Confirm(Guid eventId, Guid checkpointId, Guid observationId, ConfirmInput input, CancellationToken ct) =>
        Write(eventId, ct, async item =>
        {
            if (!await HasGrant(eventId, checkpointId, input.AssignmentGeneration, ct)) return Forbid();
            if (item.Status != EventStatus.Live) return ConflictProblem("capture_closed", "The event is closed for timing entry.");
            var observation = await db.Set<TimingObservation>().SingleOrDefaultAsync(x =>
                x.Id == observationId && x.EventId == eventId && x.CheckpointId == checkpointId, ct);
            if (observation is null) return NotFound();
            var latest = await Latest(observationId, ct);
            if (latest.Revision != input.ExpectedRevision || latest.Outcome != TimingOutcome.AmbiguousOccurrence)
                return ConflictProblem("confirmation_unavailable", "Reload the observation. Only ambiguous entries can be confirmed here.");
            if (!await Visits(observation.RaceId, checkpointId).AnyAsync(x => x.OccurrenceId == input.OccurrenceId, ct))
                return NotFound();
            var outcome = await ValidateSlot(observation, input.OccurrenceId, ct);
            return Ok(Receipt(observation, AddDecision(observation, latest.Revision + 1, input.OccurrenceId, outcome, "Operator confirmed occurrence")));
        }, manager: false);

    [HttpPost("observations/{observationId:guid}/resolve")]
    public Task<IActionResult> Resolve(Guid eventId, Guid observationId, ResolveInput input, CancellationToken ct) =>
        Write(eventId, ct, async item =>
        {
            if (item.Status is not (EventStatus.Live or EventStatus.Completed))
                return ConflictProblem("resolution_closed", "Timing resolution requires a live or completed event.");
            if (!ValidText(input.Reason, 300)) return BadRequest("Provide a resolution reason.");
            var observation = await db.Set<TimingObservation>().SingleOrDefaultAsync(x => x.EventId == eventId && x.Id == observationId, ct);
            if (observation is null) return NotFound();
            var prior = await Latest(observationId, ct);
            if (prior.Revision != input.ExpectedRevision)
                return ConflictProblem("stale_decision", "Reload the observation history before resolving it.");
            if (input.OccurrenceId is Guid occurrence)
            {
                if (prior.Outcome == TimingOutcome.Accepted)
                    return ConflictProblem("accepted_slot", "Invalidate the existing decision before accepting a different visit.");
                if (!await Visits(observation.RaceId, observation.CheckpointId).AnyAsync(x => x.OccurrenceId == occurrence, ct))
                    return NotFound();
                if (await ValidateSlot(observation, occurrence, ct) != TimingOutcome.Accepted)
                    return ConflictProblem("slot_conflict", "Resolve the occupied slot or inconsistent sequence first.");
            }
            else if (prior.Outcome == TimingOutcome.Accepted)
            {
                var slot = await db.Set<AcceptedTiming>().SingleAsync(x => x.ObservationId == observationId, ct);
                db.Remove(slot);
            }
            var outcome = input.OccurrenceId.HasValue ? TimingOutcome.Accepted : TimingOutcome.Invalidated;
            var decision = AddDecision(observation, prior.Revision + 1, input.OccurrenceId, outcome, input.Reason.Trim());
            audit.Record(User, "Timing.ObservationResolved", nameof(TimingObservation), observationId.ToString(), item.OrganizerId);
            return Ok(Receipt(observation, decision));
        });

    [HttpGet("observations/{observationId:guid}/history")]
    public Task<IActionResult> History(Guid eventId, Guid observationId, CancellationToken ct) =>
        Write(eventId, ct, async _ =>
        {
            var observation = await db.Set<TimingObservation>().SingleOrDefaultAsync(x => x.EventId == eventId && x.Id == observationId, ct);
            if (observation is null) return NotFound();
            var decisions = await db.Set<TimingDecision>().Where(x => x.ObservationId == observationId).OrderBy(x => x.Revision).ToListAsync(ct);
            return Ok(new
            {
                observation = new
                {
                    observation.Id, observation.EventId, observation.RaceId, observation.RegistrationId,
                    observation.CheckpointId, observation.ProposedOccurrenceId, observation.BibNumber,
                    observedAtUtc = Utc(observation.ObservedAtUtc), receivedAtUtc = Utc(observation.ReceivedAtUtc),
                    observation.ActorUserId, observation.DeviceId, observation.IdempotencyKey,
                    observation.AssignmentGeneration, source = observation.Source.ToString()
                },
                decisions = decisions.Select(x => new
                {
                    x.Revision, x.OccurrenceId, outcome = x.Outcome.ToString(),
                    decidedAtUtc = Utc(x.DecidedAtUtc), x.ActorUserId, x.Reason
                })
            });
        });

    private ObjectResult BibIdentityConflict() => ConflictProblem("timing_bib_identity_conflict",
        "This bib has different owners in the timing roster and current registrations. Ask race control to reconcile the assignment.");

    // Use the same event scope and database collation as the current bib uniqueness constraint.
    private Task<bool> HasConflictingLiveBibOwner(Guid eventId, Guid registrationId, string bib, CancellationToken ct) =>
        db.Registrations.AnyAsync(x => x.EventId == eventId && x.Id != registrationId && x.BibNumber == bib, ct);

    private Task<bool> HasGrant(Guid eventId, Guid checkpointId, Guid generation, CancellationToken ct) =>
        Grants.AnyAsync(x => x.EventId == eventId && x.CheckpointId == checkpointId && x.Generation == generation, ct);

    private IQueryable<TimingVisit> Visits(Guid raceId, Guid checkpointId) =>
        db.Set<TimingVisit>().Where(x => x.RaceId == raceId && x.CheckpointId == checkpointId).OrderBy(x => x.Sequence);

    private static object VisitResponse(TimingVisit x) =>
        new { x.OccurrenceId, x.Sequence, x.Name, x.DistanceKm, x.IsStart, x.IsFinish };

    private Task<TimingDecision> Latest(Guid observationId, CancellationToken ct) =>
        db.Set<TimingDecision>().Where(x => x.ObservationId == observationId).OrderByDescending(x => x.Revision).FirstAsync(ct);

    private async Task<TimingOutcome> ValidateSlot(TimingObservation observation, Guid occurrenceId, CancellationToken ct)
    {
        var visit = await db.Set<TimingVisit>().SingleAsync(x => x.RaceId == observation.RaceId && x.OccurrenceId == occurrenceId, ct);
        var slots = db.Set<AcceptedTiming>().Where(x => x.RegistrationId == observation.RegistrationId);
        if (await slots.AnyAsync(x => x.OccurrenceId == occurrenceId, ct)) return TimingOutcome.SlotConflict;
        var incompatible = from slot in slots
            join crossing in db.Set<TimingObservation>() on slot.ObservationId equals crossing.Id
            join other in db.Set<TimingVisit>() on new { slot.RaceId, slot.OccurrenceId } equals new { other.RaceId, other.OccurrenceId }
            where (other.Sequence < visit.Sequence && crossing.ObservedAtUtc > observation.ObservedAtUtc) ||
                  (other.Sequence > visit.Sequence && crossing.ObservedAtUtc < observation.ObservedAtUtc)
            select slot.ObservationId;
        return await incompatible.AnyAsync(ct) ? TimingOutcome.SequenceConflict : TimingOutcome.Accepted;
    }

    private TimingDecision AddDecision(TimingObservation observation, int revision, Guid? occurrenceId, TimingOutcome outcome, string reason)
    {
        var decision = new TimingDecision
        {
            ObservationId = observation.Id, Revision = revision, OccurrenceId = occurrenceId, Outcome = outcome,
            DecidedAtUtc = DateTime.UtcNow, ActorUserId = Actor, Reason = reason
        };
        db.Add(decision);
        if (outcome == TimingOutcome.Accepted) db.Add(TimingProjection.Accept(observation, decision));
        return decision;
    }

    private static object Receipt(TimingObservation observation, TimingDecision decision) => new
    {
        observationId = observation.Id, observation.RegistrationId, observation.RaceId,
        observedAtUtc = Utc(observation.ObservedAtUtc), receivedAtUtc = Utc(observation.ReceivedAtUtc),
        outcome = decision.Outcome.ToString(), decision.Revision, decision.OccurrenceId
    };
}
