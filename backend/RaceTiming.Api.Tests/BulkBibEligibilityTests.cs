using RaceTiming.Application;
using RaceTiming.Domain.Enums;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed class BulkBibEligibilityTests
{
    [Theory]
    [InlineData(RegistrationStatus.Pending)]
    [InlineData(RegistrationStatus.Confirmed)]
    [InlineData(RegistrationStatus.Waitlisted)]
    public void EligibleStatusesPermitAssignmentWithoutBib(RegistrationStatus status) =>
        Assert.Null(BulkBibEligibility.SkipReason(status, RecordStatus.Active, null, false));

    [Theory]
    [InlineData(RegistrationStatus.Cancelled, "Withdrawn.")]
    [InlineData(RegistrationStatus.Dnf, "Historical status.")]
    [InlineData(RegistrationStatus.Dns, "Historical status.")]
    [InlineData(RegistrationStatus.Disqualified, "Historical status.")]
    public void IneligibleStatusesHaveReasons(RegistrationStatus status, string reason) =>
        Assert.Equal(reason, BulkBibEligibility.SkipReason(status, RecordStatus.Active, null, false));

    [Theory]
    [InlineData(RecordStatus.Active, "001", false, "Bib already assigned.")]
    [InlineData(RecordStatus.Active, "001", true, "Already checked in.")]
    [InlineData(RecordStatus.Inactive, null, false, "Race is inactive.")]
    public void ExistingBibCheckInAndRaceStateAreProtected(RecordStatus race, string? bib, bool checkedIn, string reason) =>
        Assert.Equal(reason, BulkBibEligibility.SkipReason(RegistrationStatus.Confirmed, race, bib, checkedIn));

    [Theory]
    [InlineData(-1, 4, 2)]
    [InlineData(4, 3, 2)]
    [InlineData(0, 100001, 2)]
    [InlineData(1, 4, 11)]
    [InlineData(1, 4, -1)]
    [InlineData(null, 4, 1)]
    public void AutomaticAllocationRejectsInvalidBulkRanges(int? start, int end, int width) =>
        Assert.NotNull(BibAllocator.Allocate(new(BibAssignmentMode.Automatic, start, end, width), [], [null], out _));

    [Fact]
    public void ReusedAllocatorFillsGapsInOrderWithLeadingZeros()
    {
        var options = new BibAssignment(BibAssignmentMode.Automatic, 1, 8, 3);
        Assert.Null(BibAllocator.Allocate(options, ["005", "002", "001"], [null, null, null], out var assigned));
        Assert.Equal(new[] { "003", "004", "006" }, assigned);
    }
}
