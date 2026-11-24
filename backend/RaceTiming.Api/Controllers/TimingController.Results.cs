using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Application;
using RaceTiming.Domain.Entities;

namespace RaceTiming.Api.Controllers;

public sealed partial class TimingController
{
    [HttpGet("races/{raceId:guid}/results")]
    public Task<IActionResult> RaceResults(Guid eventId, Guid raceId, CancellationToken ct, int offset = 0, int limit = 25) =>
        ReadResults(eventId, raceId, ct, async setup =>
        {
            if (offset < 0 || limit is < 1 or > 100) return BadRequest("Use a nonnegative offset and a limit from 1 to 100.");
            var roster = ResultsRoster(setup);
            var total = await roster.CountAsync(ct);
            var page = await roster.OrderBy(x => x.RegistrationId).Skip(offset).Take(limit).ToListAsync(ct);
            var items = await CalculateResults(setup, page, ct);
            return Ok(new { eventId, raceId, setup.CourseId, setup.TimeZoneId, offset, limit, total, items });
        });

    [HttpGet("races/{raceId:guid}/results/{registrationId:guid}")]
    public Task<IActionResult> RegistrationResult(Guid eventId, Guid raceId, Guid registrationId, CancellationToken ct) =>
        ReadResults(eventId, raceId, ct, async setup =>
        {
            var entry = await ResultsRoster(setup).SingleOrDefaultAsync(x => x.RegistrationId == registrationId, ct);
            if (entry is null) return NotFound();
            var items = await CalculateResults(setup, [entry], ct);
            return Ok(new { eventId, raceId, setup.CourseId, setup.TimeZoneId, result = items[0] });
        });

    private IQueryable<TimingRosterEntry> ResultsRoster(RaceTimingSetup setup) =>
        db.Set<TimingRosterEntry>().AsNoTracking().Where(x => x.EventId == setup.EventId && x.RaceId == setup.RaceId);

    private Task<IActionResult> ReadResults(Guid eventId, Guid raceId, CancellationToken ct,
        Func<RaceTimingSetup, Task<IActionResult>> read) =>
        ReadRaceControl(eventId, ct, async () =>
        {
            var setup = await db.Set<RaceTimingSetup>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.EventId == eventId && x.RaceId == raceId, ct);
            return setup is null ? NotFound() : await read(setup);
        });

    private async Task<IActionResult> ReadRaceControl(Guid eventId, CancellationToken ct, Func<Task<IActionResult>> read)
    {
        // MySQL serves every nonlocking SELECT in this transaction from one consistent snapshot.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var item = await db.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == eventId, ct);
        if (item is null) return NotFound();
        if (!await ActiveAccount(ct) || !await EnabledEvents.AnyAsync(x => x.Id == eventId, ct) ||
            !await CanManageTiming(item.OrganizerId, ct))
            return Forbid();

        var result = await read();
        await transaction.CommitAsync(ct);
        return result;
    }

    private async Task<RegistrationTiming[]> CalculateResults(RaceTimingSetup setup,
        IReadOnlyList<TimingRosterEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return [];
        var ids = entries.Select(x => x.RegistrationId).ToArray();
        var waveIds = entries.Select(x => x.WaveId).OfType<Guid>().Distinct().ToArray();
        var waves = await db.Set<TimingWave>().AsNoTracking().Where(x => x.RaceId == setup.RaceId && waveIds.Contains(x.WaveId))
            .ToDictionaryAsync(x => x.WaveId, ct);
        var declarations = await db.Set<OfficialGunDeclaration>().AsNoTracking().Where(x => x.RaceId == setup.RaceId &&
            (!x.WaveId.HasValue || waveIds.Contains(x.WaveId.Value))).ToListAsync(ct);
        var visits = await db.Set<TimingVisit>().AsNoTracking().Where(x => x.EventId == setup.EventId && x.RaceId == setup.RaceId)
            .ToListAsync(ct);
        var decisions = db.Set<TimingDecision>();
        var accepted = await (from slot in db.Set<AcceptedTiming>()
            join observation in db.Set<TimingObservation>() on slot.ObservationId equals observation.Id
            join decision in decisions on new { slot.ObservationId, Revision = slot.DecisionRevision }
                equals new { decision.ObservationId, decision.Revision }
            join visit in db.Set<TimingVisit>() on new { slot.RaceId, slot.OccurrenceId } equals new { visit.RaceId, visit.OccurrenceId }
            where slot.EventId == setup.EventId && slot.RaceId == setup.RaceId && ids.Contains(slot.RegistrationId)
            select new
            {
                slot.RegistrationId,
                Evidence = new CrossingEvidence(slot.OccurrenceId, observation.Id, slot.DecisionRevision, observation.ObservedAtUtc,
                    observation.CheckpointId == visit.CheckpointId && decision.Outcome == TimingOutcome.Accepted &&
                    decision.OccurrenceId == slot.OccurrenceId &&
                    !decisions.Any(next => next.ObservationId == slot.ObservationId && next.Revision > slot.DecisionRevision))
            }).ToListAsync(ct);
        var evidence = accepted.ToLookup(x => x.RegistrationId, x => x.Evidence);
        return entries.Select(entry => TimingCalculator.Calculate(setup, entry,
            entry.WaveId is Guid waveId ? waves.GetValueOrDefault(waveId) : null,
            declarations, visits, evidence[entry.RegistrationId].ToArray())).ToArray();
    }
}
