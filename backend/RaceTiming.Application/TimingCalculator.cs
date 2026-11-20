using RaceTiming.Domain.Entities;

namespace RaceTiming.Application;

public sealed record CrossingEvidence(Guid OccurrenceId, Guid ObservationId, int DecisionRevision, DateTime ObservedAtUtc, bool IsConsistent);

public sealed record CalculatedCutoff(int LimitSeconds, string Status, string? UnavailableReason);

public sealed record CalculatedVisit(Guid OccurrenceId, Guid CheckpointId, int Sequence, string Name,
    decimal DistanceKm, bool IsStart, bool IsFinish, bool IsMandatory, DateTimeOffset? CrossingAtUtc,
    Guid? ObservationId, int? DecisionRevision, long? GunElapsedMicroseconds, long? NetElapsedMicroseconds,
    long? SegmentMicroseconds, string? GunElapsedUnavailableReason, string? NetElapsedUnavailableReason,
    CalculatedCutoff? Cutoff, IReadOnlyList<string> Flags);

public sealed record TimingReadiness(bool MissingOfficialGun, bool MissingIndividualStart, bool MissingFinish,
    IReadOnlyList<Guid> MissingMandatoryOccurrences, IReadOnlyList<Guid> UnknownCheckpointCutoffs,
    bool UnknownRaceCutoff, IReadOnlyList<string> Flags);

public sealed record ProvisionalFinish(Guid OccurrenceId, DateTimeOffset CrossingAtUtc, long? GunElapsedMicroseconds,
    long? NetElapsedMicroseconds, long? ResultsElapsedMicroseconds, string? GunElapsedUnavailableReason,
    string? NetElapsedUnavailableReason, string? ResultsElapsedUnavailableReason);

public sealed record RegistrationTiming(Guid RegistrationId, string BibNumber, Guid? WaveId, DateTimeOffset? ScheduledStartUtc,
    Guid? GunDeclarationId, DateTimeOffset? OfficialGunAtUtc, DateTimeOffset? IndividualStartAtUtc, string ResultsBasis,
    string CutoffBasis, IReadOnlyList<CalculatedVisit> Visits, ProvisionalFinish? ProvisionalFinish,
    CalculatedCutoff? RaceCutoff, TimingReadiness Readiness);

public static class TimingCalculator
{
    public static RegistrationTiming Calculate(RaceTimingSetup setup, TimingRosterEntry registration, TimingWave? wave,
        IReadOnlyList<OfficialGunDeclaration> declarations, IReadOnlyList<TimingVisit> visits,
        IReadOnlyList<CrossingEvidence> evidence)
    {
        var scopedDeclarations = declarations.Where(x => x.RaceId == setup.RaceId && x.WaveId == registration.WaveId).ToArray();
        var replaced = scopedDeclarations.Select(x => x.SupersedesId).OfType<Guid>().ToHashSet();
        var current = scopedDeclarations.Where(x => !replaced.Contains(x.Id)).ToArray();
        var gun = current.Length == 1 ? current[0] : null;
        var gunUnavailableReason = gun is not null ? null :
            scopedDeclarations.Length == 0 ? "missing_official_gun" : "inconsistent_gun_history";
        var ordered = visits.OrderBy(x => x.Sequence).ToArray();
        var crossings = evidence.ToDictionary(x => x.OccurrenceId);
        DateTime? individualStart = null;
        if (ordered.FirstOrDefault(x => x.IsStart) is { } start &&
            crossings.TryGetValue(start.OccurrenceId, out var startCrossing) && startCrossing.IsConsistent)
            individualStart = startCrossing.ObservedAtUtc;

        var splits = new List<CalculatedVisit>(ordered.Length);
        DateTime? previous = null;
        foreach (var visit in ordered)
        {
            crossings.TryGetValue(visit.OccurrenceId, out var crossing);
            var observed = crossing is { IsConsistent: true } ? crossing.ObservedAtUtc : (DateTime?)null;
            var gunElapsed = Difference(observed, gun?.GunAtUtc);
            var netElapsed = Difference(observed, individualStart);
            var segment = Difference(observed, previous);
            var crossingUnavailableReason = observed.HasValue ? null :
                crossing is null ? "missing_crossing" : "inconsistent_accepted_evidence";
            var gunReason = crossingUnavailableReason ?? gunUnavailableReason;
            var netReason = crossingUnavailableReason ?? (individualStart.HasValue ? null : "missing_individual_start");
            var flags = new List<string>();
            if (crossing is { IsConsistent: false }) flags.Add("inconsistent_accepted_evidence");
            if (observed is null) flags.Add("missing_crossing");
            if (observed is DateTime at && (at < setup.WindowStartUtc || at > setup.WindowEndUtc))
                flags.Add("outside_frozen_window");
            if (gunElapsed < 0) flags.Add("before_official_gun");
            if (netElapsed < 0) flags.Add("before_individual_start");
            if (segment < 0) flags.Add("invalid_segment_chronology");
            if (segment == 0) flags.Add("identical_adjacent_timestamps");
            if (observed.HasValue && previous is null && splits.Count > 0) flags.Add("missing_preceding_crossing");
            splits.Add(new CalculatedVisit(visit.OccurrenceId, visit.CheckpointId, visit.Sequence, visit.Name, visit.DistanceKm,
                visit.IsStart, visit.IsFinish, visit.IsMandatory, Utc(observed), crossing?.ObservationId, crossing?.DecisionRevision,
                gunElapsed, netElapsed, segment < 0 ? null : segment, gunReason, netReason,
                Cutoff(visit.CutoffDurationSeconds, setup.CutoffBasis == TimingBasis.Gun ? gunElapsed : netElapsed,
                    setup.CutoffBasis == TimingBasis.Gun ? gunReason : netReason), flags));
            previous = observed;
        }

        var finish = splits.FirstOrDefault(x => x.IsFinish);
        var provisional = finish?.CrossingAtUtc is DateTimeOffset finishAt
            ? new ProvisionalFinish(finish.OccurrenceId, finishAt, finish.GunElapsedMicroseconds, finish.NetElapsedMicroseconds,
                setup.ResultsBasis == TimingBasis.Gun ? finish.GunElapsedMicroseconds : finish.NetElapsedMicroseconds,
                finish.GunElapsedUnavailableReason, finish.NetElapsedUnavailableReason,
                setup.ResultsBasis == TimingBasis.Gun ? finish.GunElapsedUnavailableReason : finish.NetElapsedUnavailableReason)
            : null;
        var raceCutoff = Cutoff(setup.CutoffDurationSeconds,
            setup.CutoffBasis == TimingBasis.Gun ? finish?.GunElapsedMicroseconds : finish?.NetElapsedMicroseconds,
            finish is null ? "missing_crossing" : setup.CutoffBasis == TimingBasis.Gun
                ? finish.GunElapsedUnavailableReason : finish.NetElapsedUnavailableReason);
        var readiness = new TimingReadiness(gun is null, individualStart is null, provisional is null,
            splits.Where(x => x.IsMandatory && x.CrossingAtUtc is null).Select(x => x.OccurrenceId).ToArray(),
            splits.Where(x => x.Cutoff?.Status == "unknown").Select(x => x.OccurrenceId).ToArray(),
            raceCutoff?.Status == "unknown",
            scopedDeclarations.Length > 0 && current.Length != 1 ? ["inconsistent_gun_history"] : []);
        return new RegistrationTiming(registration.RegistrationId, registration.BibNumber, registration.WaveId,
            Utc(registration.WaveId.HasValue ? wave?.ScheduledGunUtc : setup.ScheduledGunUtc),
            gun?.Id, Utc(gun?.GunAtUtc), Utc(individualStart), setup.ResultsBasis.ToString(), setup.CutoffBasis.ToString(),
            splits, provisional, raceCutoff, readiness);
    }

    private static long? Difference(DateTime? end, DateTime? start) => end.HasValue && start.HasValue
        ? (end.Value - start.Value).Ticks / 10
        : null;

    private static DateTimeOffset? Utc(DateTime? value) => value is DateTime at
        ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc))
        : null;

    private static CalculatedCutoff? Cutoff(int? seconds, long? elapsedMicroseconds, string? unavailableReason)
    {
        if (seconds is not int limit) return null;
        if (elapsedMicroseconds is not long elapsed) return new CalculatedCutoff(limit, "unknown", unavailableReason);
        if (elapsed < 0) return new CalculatedCutoff(limit, "unknown", "invalid_chronology");
        return new CalculatedCutoff(limit, elapsed <= limit * 1_000_000L ? "within_limit" : "over_limit", null);
    }
}
