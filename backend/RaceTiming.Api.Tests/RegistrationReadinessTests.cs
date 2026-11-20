using RaceTiming.Application;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed class RegistrationReadinessTests
{
    [Fact]
    public void CheckInProgressDoesNotBlockRegistrationReadiness()
    {
        var counts = new ReadinessCounts(Total: 2, Confirmed: 2, BibsAssigned: 2, ConfirmedNotCheckedIn: 2);
        Assert.Equal("ready", counts.Assess().Level);
        Assert.Empty(counts.Assess().Warnings);
    }

    [Fact]
    public void BlockersTakePrecedenceOverAttentionAcrossRaces()
    {
        var counts = ReadinessCounts.Sum([
            new(Total: 1, Pending: 1, EligibleWithoutBib: 1, PendingOrWaitlistedEligibleWithoutBib: 1),
            new(Total: 2, Confirmed: 2, ConfirmedWithoutBib: 1, InactiveRaceConfirmed: 2, InactiveRacesWithRegistrations: 1)
        ]);
        Assert.Equal(3, counts.Total);
        Assert.Equal(2, counts.Confirmed);
        var assessment = counts.Assess();
        Assert.Equal("actionRequired", assessment.Level);
        Assert.Contains(assessment.Warnings, warning => warning.Level == "attention");
        Assert.Equal("attention", new ReadinessCounts().Assess().Level);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("0007", "0007")]
    [InlineData("Ana-Marie", "Ana-Marie")]
    [InlineData("José, \"Jo\"", "José, \"Jo\"")]
    [InlineData("Team\nNorth", "Team\nNorth")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData(" +1", "' +1")]
    [InlineData("-7", "'-7")]
    [InlineData("@FORMULA", "'@FORMULA")]
    [InlineData("\tplain", "'\tplain")]
    [InlineData("\rplain", "'\rplain")]
    [InlineData("\nplain", "'\nplain")]
    [InlineData("\uFEFF＝1", "'\uFEFF＝1")]
    [InlineData("\u200B=1", "'\u200B=1")]
    public void CsvEscapesFormulaPrefixesAndPreservesOrdinaryFields(string? input, string expected) =>
        Assert.Equal(expected, RosterCsv.SafeField(input));
}
