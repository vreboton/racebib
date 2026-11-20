using RaceTiming.Domain.Enums;
using RaceTiming.Domain.Services;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed class RegistrationCheckInTests
{
    [Theory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Published)]
    [InlineData(EventStatus.Completed)]
    [InlineData(EventStatus.Cancelled)]
    public void CheckInIsUnavailableOutsideOperationalStates(EventStatus status) =>
        Assert.NotNull(RegistrationCheckIn.GetIneligibility(status, RegistrationStatus.Confirmed, RecordStatus.Active, "0007"));

    [Theory]
    [InlineData(EventStatus.RegistrationOpen)]
    [InlineData(EventStatus.RegistrationClosed)]
    [InlineData(EventStatus.Ready)]
    [InlineData(EventStatus.Live)]
    public void ConfirmedRunnerWithBibCanCheckInDuringOperations(EventStatus status) =>
        Assert.Null(RegistrationCheckIn.GetIneligibility(status, RegistrationStatus.Confirmed, RecordStatus.Active, "0007"));

    [Theory]
    [InlineData(RegistrationStatus.Pending)]
    [InlineData(RegistrationStatus.Waitlisted)]
    [InlineData(RegistrationStatus.Cancelled)]
    [InlineData(RegistrationStatus.Dns)]
    [InlineData(RegistrationStatus.Dnf)]
    [InlineData(RegistrationStatus.Disqualified)]
    public void OnlyConfirmedRegistrationsAreEligible(RegistrationStatus status) =>
        Assert.Contains("confirmed", RegistrationCheckIn.GetIneligibility(EventStatus.RegistrationOpen, status, RecordStatus.Active, "0007"));

    [Theory]
    [InlineData(RecordStatus.Active, null, "bib")]
    [InlineData(RecordStatus.Active, " ", "bib")]
    [InlineData(RecordStatus.Inactive, "0007", "inactive")]
    public void RequiresActiveRaceAndAssignedBib(RecordStatus race, string? bib, string reason) =>
        Assert.Contains(reason, RegistrationCheckIn.GetIneligibility(EventStatus.RegistrationOpen, RegistrationStatus.Confirmed, race, bib));
}
