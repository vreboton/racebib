using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Application;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;

namespace RaceTiming.Api.Controllers;

public sealed partial class TimingController
{
    private IQueryable<TimingDecision> LatestDecisions => db.Set<TimingDecision>().Where(x =>
        !db.Set<TimingDecision>().Any(next => next.ObservationId == x.ObservationId && next.Revision > x.Revision));

    private IQueryable<OfficialRunnerDecision> OfficialDecisions(Guid eventId) =>
        db.Set<OfficialRunnerDecision>().AsNoTracking().Where(x => x.EventId == eventId);

    private IQueryable<TimingRosterEntry> BibConflicts(Guid eventId) =>
        db.Set<TimingRosterEntry>().Where(x => x.EventId == eventId && db.Registrations.Any(live =>
            live.EventId == eventId && live.Id != x.RegistrationId && live.BibNumber == x.BibNumber));

    private static bool GoodPage(int offset, int limit) => offset >= 0 && limit is >= 1 and <= 100;

    [HttpGet("control")]
    public Task<IActionResult> ControlOverview(Guid eventId, CancellationToken ct) =>
        ReadRaceControl(eventId, ct, async () =>
        {
            var races = await (from setup in db.Set<RaceTimingSetup>()
                join race in db.Races on setup.RaceId equals race.Id
                where setup.EventId == eventId
                orderby race.Name, race.Id
                select new { raceId = race.Id, race.Name, setup.TimeZoneId,
                    registered = db.Set<TimingRosterEntry>().Count(x => x.EventId == eventId && x.RaceId == race.Id),
                    acceptedVisits = db.Set<AcceptedTiming>().Count(x => x.EventId == eventId && x.RaceId == race.Id),
                    recordedFinishes = db.Set<AcceptedTiming>().Count(x => x.EventId == eventId && x.RaceId == race.Id &&
                        db.Set<TimingVisit>().Any(v => v.RaceId == race.Id && v.OccurrenceId == x.OccurrenceId && v.IsFinish)) })
                .ToListAsync(ct);
            var actionable = await Exceptions(eventId).CountAsync(ct);
            return Ok(new { races, actionable, bibConflicts = await BibConflicts(eventId).CountAsync(ct) });
        });

    private IQueryable<EvidenceException> Exceptions(Guid eventId) =>
        from decision in LatestDecisions
        join observation in db.Set<TimingObservation>() on decision.ObservationId equals observation.Id
        where observation.EventId == eventId &&
            (decision.Outcome == TimingOutcome.AmbiguousOccurrence || decision.Outcome == TimingOutcome.SlotConflict ||
             decision.Outcome == TimingOutcome.SequenceConflict || decision.Outcome == TimingOutcome.SuspiciousClock)
        select new EvidenceException
        {
            ObservationId = observation.Id, RaceId = observation.RaceId, RegistrationId = observation.RegistrationId,
            BibNumber = observation.BibNumber, ObservedAtUtc = observation.ObservedAtUtc,
            ReceivedAtUtc = observation.ReceivedAtUtc, Outcome = decision.Outcome, Reason = decision.Reason,
            Revision = decision.Revision
        };

    private sealed class EvidenceException
    {
        public Guid ObservationId { get; init; }
        public Guid RaceId { get; init; }
        public Guid RegistrationId { get; init; }
        public required string BibNumber { get; init; }
        public DateTime ObservedAtUtc { get; init; }
        public required string Reason { get; init; }
        public DateTime ReceivedAtUtc { get; init; }
        public TimingOutcome Outcome { get; init; }
        public int Revision { get; init; }
    }

    [HttpGet("control/exceptions")]
    public Task<IActionResult> ControlExceptions(Guid eventId, CancellationToken ct, string kind = "evidence", int offset = 0, int limit = 25) =>
        ReadRaceControl(eventId, ct, async () =>
        {
            if (!GoodPage(offset, limit)) return BadRequest("Use a nonnegative offset and a limit from 1 to 100.");
            if (kind == "bib")
            {
                var conflicts = BibConflicts(eventId);
                var items = await conflicts.OrderBy(x => x.RegistrationId).Skip(offset).Take(limit)
                    .Select(x => new { x.RegistrationId, x.RaceId, x.BibNumber }).ToListAsync(ct);
                return Ok(new { offset, limit, total = await conflicts.CountAsync(ct), items });
            }
            if (kind != "evidence") return BadRequest("Choose evidence or bib.");
            var query = Exceptions(eventId);
            var rows = await query.OrderByDescending(x => x.ReceivedAtUtc).ThenBy(x => x.ObservationId)
                .Skip(offset).Take(limit).ToListAsync(ct);
            var raceIds = rows.Where(x => x.Outcome == TimingOutcome.SuspiciousClock).Select(x => x.RaceId).Distinct().ToArray();
            var setups = await db.Set<RaceTimingSetup>().Where(x => x.EventId == eventId && raceIds.Contains(x.RaceId))
                .ToDictionaryAsync(x => x.RaceId, ct);
            return Ok(new { offset, limit, total = await query.CountAsync(ct), items = rows.Select(x => new
            {
                x.ObservationId, x.RaceId, x.RegistrationId, x.BibNumber, x.Revision,
                receivedAtUtc = Utc(x.ReceivedAtUtc), outcome = x.Outcome.ToString(),
                diagnostic = CaptureDiagnostic(x.ObservedAtUtc, x.ReceivedAtUtc, x.Revision, x.Outcome, x.Reason,
                    setups.GetValueOrDefault(x.RaceId))
            }) });
        });

    [HttpGet("control/races/{raceId:guid}/runners")]
    public Task<IActionResult> ControlRunners(Guid eventId, Guid raceId, CancellationToken ct, int offset = 0, int limit = 25) =>
        ReadResults(eventId, raceId, ct, async setup =>
        {
            if (!GoodPage(offset, limit)) return BadRequest("Use a nonnegative offset and a limit from 1 to 100.");
            var roster = ResultsRoster(setup);
            var entries = await roster.OrderBy(x => x.RegistrationId).Skip(offset).Take(limit).ToListAsync(ct);
            var ids = entries.Select(x => x.RegistrationId).ToArray();
            var decisions = OfficialDecisions(eventId).Where(x => x.RaceId == raceId);
            var latest = await decisions.Where(x => ids.Contains(x.RegistrationId) &&
                !decisions.Any(next => next.RegistrationId == x.RegistrationId && next.Revision > x.Revision))
                .ToDictionaryAsync(x => x.RegistrationId, ct);
            var conflicts = await BibConflicts(eventId).Where(x => ids.Contains(x.RegistrationId))
                .Select(x => x.RegistrationId).ToListAsync(ct);
            var results = await CalculateResults(setup, entries, ct);
            var items = results.Select(timing =>
            {
                var decision = latest.GetValueOrDefault(timing.RegistrationId);
                var bibConflict = conflicts.Contains(timing.RegistrationId);
                return new
                {
                    timing, warnings = CalculationWarnings(timing), bibConflict, classification = (decision?.Classification ?? OfficialClassification.Pending).ToString(),
                    revision = decision?.Revision ?? 0,
                    needsReview = decision is { Classification: not OfficialClassification.Pending } &&
                        decision.EvidenceFingerprint != OfficialTimingPolicy.Fingerprint(timing, bibConflict)
                };
            }).ToArray();
            return Ok(new { offset, limit, total = await roster.CountAsync(ct), items, setup.TimeZoneId });
        });

    private static IReadOnlyList<string> CalculationWarnings(RegistrationTiming timing)
    {
        var warnings = new List<string>();
        if (timing.Readiness.MissingOfficialGun) warnings.Add("missing_official_gun");
        if (timing.Readiness.MissingIndividualStart) warnings.Add("missing_individual_start");
        if (timing.Readiness.MissingMandatoryOccurrences.Count > 0) warnings.Add("missing_mandatory_evidence");
        if (timing.RaceCutoff?.Status == "over_limit" || timing.Visits.Any(x => x.Cutoff?.Status == "over_limit"))
            warnings.Add("calculated_cutoff_exceeded");
        return warnings;
    }

    public sealed record ClassificationInput(OfficialClassification? Classification, int? ExpectedRevision, string Reason);

    [HttpPost("control/races/{raceId:guid}/runners/{registrationId:guid}/classification")]
    public Task<IActionResult> Classify(Guid eventId, Guid raceId, Guid registrationId, ClassificationInput input, CancellationToken ct) =>
        Write(eventId, ct, async item =>
        {
            if (input.Classification is not OfficialClassification classification || !Enum.IsDefined(classification) ||
                input.ExpectedRevision is not int expectedRevision || expectedRevision < 0 || !ValidText(input.Reason, 300))
                return BadRequest("Provide a classification, expected revision and reason of up to 300 characters.");
            var setup = await db.Set<RaceTimingSetup>().SingleOrDefaultAsync(x => x.EventId == eventId && x.RaceId == raceId, ct);
            if (setup is null) return NotFound();
            var entry = await ResultsRoster(setup).SingleOrDefaultAsync(x => x.RegistrationId == registrationId, ct);
            if (entry is null) return NotFound();
            if (item.Status is not (EventStatus.Live or EventStatus.Completed))
                return ConflictProblem("classification_closed", "Official classification requires a live or completed event.");
            var last = await OfficialDecisions(eventId).Where(x => x.RegistrationId == registrationId)
                .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct);
            if ((last?.Revision ?? 0) != expectedRevision)
                return ConflictProblem("stale_classification", "Reload the runner before changing the official classification.");

            var timing = (await CalculateResults(setup, [entry], ct))[0];
            var bibConflict = await HasConflictingLiveBibOwner(eventId, entry.RegistrationId, entry.BibNumber, ct);
            if (!OfficialTimingPolicy.CanClassify(classification, timing, bibConflict))
                return ConflictProblem("finish_evidence_required", "Finished requires an accepted finish crossing and a reconciled bib.");
            var decision = new OfficialRunnerDecision
            {
                EventId = eventId, RaceId = raceId, RegistrationId = registrationId, Revision = expectedRevision + 1,
                Classification = classification, ActorUserId = Actor, Reason = input.Reason.Trim(),
                DecidedAtUtc = Instant(DateTimeOffset.UtcNow), EvidenceFingerprint = OfficialTimingPolicy.Fingerprint(timing, bibConflict)
            };
            db.Add(decision);
            audit.Record(User, "Timing.ClassificationDecided", nameof(OfficialRunnerDecision), registrationId.ToString(), item.OrganizerId);
            return StatusCode(201, new { decision.Revision, classification = decision.Classification.ToString() });
        });

    [HttpGet("control/audit")]
    public Task<IActionResult> ControlAudit(Guid eventId, CancellationToken ct, string kind = "classification", int offset = 0, int limit = 25) =>
        ReadRaceControl(eventId, ct, async () =>
        {
            if (!GoodPage(offset, limit)) return BadRequest("Use a nonnegative offset and a limit from 1 to 100.");
            if (kind == "evidence")
            {
                var evidence = from decision in db.Set<TimingDecision>()
                    join observation in db.Set<TimingObservation>() on decision.ObservationId equals observation.Id
                    where observation.EventId == eventId
                    select new { observation.BibNumber, decision };
                var rows = await evidence.OrderByDescending(x => x.decision.DecidedAtUtc).ThenBy(x => x.decision.ObservationId)
                    .ThenByDescending(x => x.decision.Revision).Skip(offset).Take(limit).ToListAsync(ct);
                return Ok(new { offset, limit, total = await evidence.CountAsync(ct), items = rows.Select(x => new
                {
                    x.BibNumber, x.decision.ObservationId, x.decision.Revision, x.decision.ActorUserId, x.decision.Reason,
                    classification = x.decision.Outcome.ToString(), decidedAtUtc = Utc(x.decision.DecidedAtUtc)
                }) });
            }
            if (kind != "classification") return BadRequest("Choose classification or evidence.");
            var records = OfficialDecisions(eventId);
            var decisions = await (from decision in records
                join roster in db.Set<TimingRosterEntry>() on new { decision.EventId, decision.RegistrationId }
                    equals new { roster.EventId, roster.RegistrationId }
                orderby decision.DecidedAtUtc descending, decision.RegistrationId, decision.Revision descending
                select new { decision, roster.BibNumber, Previous = records.Where(x => x.RegistrationId == decision.RegistrationId &&
                    x.Revision == decision.Revision - 1).Select(x => (OfficialClassification?)x.Classification).FirstOrDefault() })
                .Skip(offset).Take(limit).ToListAsync(ct);
            return Ok(new { offset, limit, total = await records.CountAsync(ct), items = decisions.Select(x => new
            {
                x.BibNumber, x.decision.RegistrationId, x.decision.Revision, x.decision.ActorUserId, x.decision.Reason,
                classification = x.decision.Classification.ToString(), previousClassification = (x.Previous ?? OfficialClassification.Pending).ToString(),
                decidedAtUtc = Utc(x.decision.DecidedAtUtc)
            }) });
        });

    [HttpGet("control/observations/{observationId:guid}/resolution")]
    public Task<IActionResult> ResolutionOptions(Guid eventId, Guid observationId, CancellationToken ct) =>
        ReadRaceControl(eventId, ct, async () =>
        {
            var observation = await db.Set<TimingObservation>().SingleOrDefaultAsync(x => x.EventId == eventId && x.Id == observationId, ct);
            if (observation is null) return NotFound();
            var decisions = await db.Set<TimingDecision>().Where(x => x.ObservationId == observationId)
                .OrderBy(x => x.Revision).ToListAsync(ct);
            var setup = await db.Set<RaceTimingSetup>().SingleOrDefaultAsync(x => x.RaceId == observation.RaceId, ct);
            var accepted = await db.Set<AcceptedTiming>().Where(x => x.RegistrationId == observation.RegistrationId)
                .ToDictionaryAsync(x => x.OccurrenceId, ct);
            var visits = await Visits(observation.RaceId, observation.CheckpointId).ToListAsync(ct);
            var choices = new List<object>();
            var bibConflict = await HasConflictingLiveBibOwner(eventId, observation.RegistrationId, observation.BibNumber, ct);
            foreach (var visit in visits)
            {
                var slot = accepted.GetValueOrDefault(visit.OccurrenceId);
                var effect = slot?.ObservationId == observationId ? "AlreadyAccepted" :
                    bibConflict ? "BibIdentityConflict" : (await ValidateSlot(observation, visit.OccurrenceId, ct)).ToString();
                choices.Add(new { visit.OccurrenceId, visit.Sequence, visit.Name, effect, currentObservationId = slot?.ObservationId });
            }
            return Ok(new
            {
                observation.Id, observation.BibNumber, observedAtUtc = Utc(observation.ObservedAtUtc),
                receivedAtUtc = Utc(observation.ReceivedAtUtc), acceptedOccurrenceId = accepted.Values
                    .FirstOrDefault(x => x.ObservationId == observationId)?.OccurrenceId,
                diagnostic = CaptureDiagnostic(observation, decisions.FirstOrDefault(), setup),
                choices, decisions = decisions.Select(x => new
                {
                    x.Revision, x.OccurrenceId, outcome = x.Outcome.ToString(), x.Reason, x.ActorUserId, decidedAtUtc = Utc(x.DecidedAtUtc)
                })
            });
        });
}
