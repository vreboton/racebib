using RaceTiming.Application;
using RaceTiming.Domain.Entities;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed class TimingCalculatorTests
{
    private static readonly DateTime RaceStart = new(2026, 11, 20, 23, 59, 59, DateTimeKind.Utc);
    private readonly Guid raceId = Guid.NewGuid();
    private readonly Guid registrationId = Guid.NewGuid();
    private readonly Guid waveId = Guid.NewGuid();

    [Fact]
    public void SelectsOnlyTheApplicableOfficialGunAndKeepsNetIndependent()
    {
        var visits = Route();
        var wave = new TimingWave { RaceId = raceId, WaveId = waveId, ScheduledGunUtc = RaceStart.AddMinutes(15) };
        var raceGun = Gun(RaceStart);
        var oldWaveGun = Gun(RaceStart.AddMinutes(10), waveId);
        var waveGun = Gun(RaceStart.AddMinutes(20), waveId, oldWaveGun.Id);
        var evidence = new[] { At(visits[0], RaceStart.AddMinutes(19)), At(visits[3], RaceStart.AddMinutes(25)) };

        var result = Calculate(visits, evidence, [waveGun, raceGun, oldWaveGun], wave);
        Assert.Equal(waveGun.Id, result.GunDeclarationId);
        Assert.Equal(300_000_000L, result.ProvisionalFinish!.GunElapsedMicroseconds);
        Assert.Equal(360_000_000L, result.ProvisionalFinish.NetElapsedMicroseconds);
        Assert.Contains("before_official_gun", result.Visits[0].Flags);
        Assert.Equal(-60_000_000L, result.Visits[0].GunElapsedMicroseconds);
        Assert.Null(result.Visits[0].GunElapsedUnavailableReason);

        var noWave = Calculate(visits, evidence, [oldWaveGun, waveGun, raceGun]);
        Assert.Equal(raceGun.Id, noWave.GunDeclarationId);
        Assert.Equal(1_500_000_000L, noWave.ProvisionalFinish!.GunElapsedMicroseconds);

        var undeclaredWave = Calculate(visits, evidence, [raceGun], wave);
        Assert.True(undeclaredWave.Readiness.MissingOfficialGun);
        Assert.NotNull(undeclaredWave.ScheduledStartUtc);
        Assert.Null(undeclaredWave.ProvisionalFinish!.GunElapsedMicroseconds);
        Assert.Equal("missing_official_gun", undeclaredWave.ProvisionalFinish.GunElapsedUnavailableReason);
        Assert.Equal(360_000_000L, undeclaredWave.ProvisionalFinish.NetElapsedMicroseconds);
    }

    [Fact]
    public void CalculatesMicrosecondPrecisionAcrossDaysAndDoesNotBridgeMissingVisits()
    {
        var visits = Route();
        var start = At(visits[0], RaceStart.AddTicks(10));
        var firstVisit = At(visits[1], RaceStart.AddHours(24));
        var finish = At(visits[3], RaceStart.AddHours(30).AddTicks(20));
        var gun = Gun(RaceStart);
        var result = Calculate(visits.Reverse().ToArray(), [finish, start, firstVisit], [gun]);

        Assert.Equal(108_000_000_002L, result.ProvisionalFinish!.GunElapsedMicroseconds);
        Assert.Equal(108_000_000_001L, result.ProvisionalFinish.NetElapsedMicroseconds);
        Assert.Equal(86_399_999_999L, result.Visits[1].SegmentMicroseconds);
        Assert.Equal(visits[1].CheckpointId, result.Visits[2].CheckpointId);
        Assert.NotEqual(visits[1].OccurrenceId, result.Visits[2].OccurrenceId);
        Assert.Null(result.Visits[2].CrossingAtUtc);
        Assert.Null(result.Visits[3].SegmentMicroseconds);
        Assert.Contains("missing_preceding_crossing", result.Visits[3].Flags);
        Assert.Equal(new[] { visits[2].OccurrenceId }, result.Readiness.MissingMandatoryOccurrences);

        var late = At(visits[2], RaceStart.AddHours(27));
        var updated = Calculate(visits, [late, firstVisit, finish, start], [gun]);
        Assert.Empty(updated.Readiness.MissingMandatoryOccurrences);
        Assert.Equal(10_800_000_002L, updated.Visits[3].SegmentMicroseconds);
    }

    [Fact]
    public void CutoffsAreInclusiveAndUnknownWithoutTheSelectedBasis()
    {
        var visits = Route();
        var gun = Gun(RaceStart);
        var finish = At(visits[3], RaceStart.AddMinutes(1));
        var result = Calculate(visits, [finish], [gun]);
        Assert.Equal("within_limit", result.RaceCutoff!.Status);
        Assert.Equal("unknown", result.Visits[1].Cutoff!.Status);
        Assert.Equal("missing_crossing", result.Visits[1].Cutoff!.UnavailableReason);
        Assert.Null(result.RaceCutoff.UnavailableReason);
        Assert.True(result.Readiness.MissingIndividualStart);
        Assert.Null(result.ProvisionalFinish!.NetElapsedMicroseconds);

        var over = Calculate(visits, [finish with { ObservedAtUtc = finish.ObservedAtUtc.AddTicks(10) }], [gun]);
        Assert.Equal("over_limit", over.RaceCutoff!.Status);

        var netBasis = Calculate(visits, [finish], [gun], cutoffBasis: TimingBasis.Net, resultsBasis: TimingBasis.Net);
        Assert.True(netBasis.Readiness.UnknownRaceCutoff);
        Assert.Null(netBasis.ProvisionalFinish!.ResultsElapsedMicroseconds);
        Assert.Equal("missing_individual_start", netBasis.ProvisionalFinish.ResultsElapsedUnavailableReason);
        Assert.Equal("missing_individual_start", netBasis.RaceCutoff!.UnavailableReason);
        Assert.Equal(60_000_000L, netBasis.ProvisionalFinish.GunElapsedMicroseconds);

        var undeclared = Calculate(visits, [finish], []);
        Assert.True(undeclared.Readiness.MissingOfficialGun);
        Assert.True(undeclared.Readiness.UnknownRaceCutoff);
        Assert.Null(undeclared.ProvisionalFinish!.GunElapsedMicroseconds);
        Assert.Equal("missing_official_gun", undeclared.RaceCutoff!.UnavailableReason);
    }

    [Fact]
    public void FlagsInconsistentCrossingsAndChronologyWithoutFabricatingSplits()
    {
        var visits = Route();
        var start = At(visits[0], RaceStart.AddSeconds(10));
        var early = At(visits[1], RaceStart.AddSeconds(9));
        var tied = At(visits[2], early.ObservedAtUtc);
        var invalidFinish = At(visits[3], RaceStart.AddSeconds(12)) with { IsConsistent = false };
        var result = Calculate(visits, [invalidFinish, tied, start, early], [Gun(RaceStart)]);
        Assert.Contains("before_individual_start", result.Visits[1].Flags);
        Assert.Contains("invalid_segment_chronology", result.Visits[1].Flags);
        Assert.Null(result.Visits[1].SegmentMicroseconds);
        Assert.Equal(0L, result.Visits[2].SegmentMicroseconds);
        Assert.Contains("identical_adjacent_timestamps", result.Visits[2].Flags);
        Assert.Contains("inconsistent_accepted_evidence", result.Visits[3].Flags);
        Assert.Null(result.ProvisionalFinish);
        Assert.True(result.Readiness.MissingFinish);
    }

    [Fact]
    public void DurationCutoffMovesWithTheSelectedStartAtTheSameFinishInstant()
    {
        var visits = Route();
        var finish = At(visits[3], RaceStart.AddSeconds(60));
        var start = At(visits[0], RaceStart);
        var originalGun = Gun(RaceStart);
        var correctedGun = Gun(RaceStart.AddTicks(-10), supersedes: originalGun.Id);
        var original = Calculate(visits, [start, finish], [originalGun]);
        var corrected = Calculate(visits, [start, finish], [originalGun, correctedGun]);
        var correctedNet = Calculate(visits, [start, finish], [originalGun, correctedGun], cutoffBasis: TimingBasis.Net);

        Assert.Equal(original.ProvisionalFinish!.CrossingAtUtc, corrected.ProvisionalFinish!.CrossingAtUtc);
        Assert.Equal("within_limit", original.RaceCutoff!.Status);
        Assert.Equal("over_limit", corrected.RaceCutoff!.Status);
        Assert.Equal("within_limit", correctedNet.RaceCutoff!.Status);

        var beforeGun = Calculate(visits, [finish], [Gun(finish.ObservedAtUtc.AddTicks(10))]);
        Assert.Equal(-1L, beforeGun.ProvisionalFinish!.GunElapsedMicroseconds);
        Assert.Equal("unknown", beforeGun.RaceCutoff!.Status);
        Assert.Equal("invalid_chronology", beforeGun.RaceCutoff.UnavailableReason);
    }

    private RegistrationTiming Calculate(TimingVisit[] visits, CrossingEvidence[] evidence,
        OfficialGunDeclaration[] guns, TimingWave? wave = null, TimingBasis cutoffBasis = TimingBasis.Gun,
        TimingBasis resultsBasis = TimingBasis.Gun) =>
        TimingCalculator.Calculate(new RaceTimingSetup
        {
            RaceId = raceId, TimeZoneId = "UTC", ScheduledGunUtc = RaceStart, CutoffDurationSeconds = 60,
            WindowStartUtc = RaceStart.AddHours(-1), WindowEndUtc = RaceStart.AddDays(2), CutoffBasis = cutoffBasis,
            ResultsBasis = resultsBasis
        }, new TimingRosterEntry { RegistrationId = registrationId, RaceId = raceId, WaveId = wave?.WaveId, BibNumber = "0042" },
        wave, guns, visits, evidence);

    private TimingVisit[] Route()
    {
        var station = Guid.NewGuid();
        return Enumerable.Range(1, 4).Select(i => new TimingVisit
        {
            RaceId = raceId, OccurrenceId = Guid.NewGuid(), CheckpointId = i is 2 or 3 ? station : Guid.NewGuid(),
            Sequence = i, IsStart = i == 1, IsFinish = i == 4, IsMandatory = true, Name = $"Visit {i}",
            CutoffDurationSeconds = i == 2 ? 60 : null
        }).ToArray();
    }

    private OfficialGunDeclaration Gun(DateTime at, Guid? wave = null, Guid? supersedes = null) => new()
    {
        RaceId = raceId, WaveId = wave, GunAtUtc = at, SupersedesId = supersedes, ActorUserId = "manager", Reason = "Test"
    };

    private static CrossingEvidence At(TimingVisit visit, DateTime at) => new(visit.OccurrenceId, Guid.NewGuid(), 1, at, true);
}
