using RaceTiming.Application;
using RaceTiming.Domain.Entities;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed class OfficialTimingPolicyTests
{
    [Fact]
    public void FinishedRequiresUsableFinishAndReconciledBib()
    {
        var timing = Result(null);
        Assert.False(OfficialTimingPolicy.CanClassify(OfficialClassification.Finished, timing, false));
        Assert.True(OfficialTimingPolicy.CanClassify(OfficialClassification.Dnf, timing, false));
        Assert.True(OfficialTimingPolicy.CanClassify(OfficialClassification.Pending, timing, true));
        timing = Result(new ProvisionalFinish(Guid.NewGuid(), DateTimeOffset.UnixEpoch, null, null, null,
            "missing_official_gun", "missing_individual_start", "missing_official_gun"));
        Assert.True(OfficialTimingPolicy.CanClassify(OfficialClassification.Finished, timing, false));
        Assert.False(OfficialTimingPolicy.CanClassify(OfficialClassification.Finished, timing, true));
    }

    [Fact]
    public void EvidenceComparisonTracksRevisionAndBibChangesWithoutDependingOnInputOrder()
    {
        var visit = Visit(1);
        var other = Visit(2);
        var original = Result(null) with { Visits = [visit, other] };
        var fingerprint = OfficialTimingPolicy.Fingerprint(original, false);

        Assert.Equal(fingerprint, OfficialTimingPolicy.Fingerprint(original with { Visits = [other, visit] }, false));
        Assert.NotEqual(fingerprint, OfficialTimingPolicy.Fingerprint(original, true));
        Assert.NotEqual(fingerprint, OfficialTimingPolicy.Fingerprint(original with { GunDeclarationId = Guid.NewGuid() }, false));
        Assert.NotEqual(fingerprint, OfficialTimingPolicy.Fingerprint(original with { Visits = [visit with { DecisionRevision = 2 }, other] }, false));
    }

    private static RegistrationTiming Result(ProvisionalFinish? finish) =>
        new(Guid.NewGuid(), "0042", null, null, null, null, null, "Gun", "Gun", [], finish, null,
            new TimingReadiness(true, true, finish is null, [], [], false, []));

    private static CalculatedVisit Visit(int sequence) =>
        new(Guid.NewGuid(), Guid.NewGuid(), sequence, "Visit", 0, false, false, true, DateTimeOffset.UnixEpoch,
            Guid.NewGuid(), 1, null, null, null, "missing_official_gun", "missing_individual_start", null, []);
}
