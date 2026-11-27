using RaceTiming.Domain.Services;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed class TimingWindowPolicyTests
{
    private static readonly DateTime Start = new(2026, 11, 27, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddHours(5);

    [Theory]
    [InlineData(-1, 0, TimingWindowPolicy.BeforeWindow)]
    [InlineData(0, 0, null)]
    [InlineData(18000000000L, 18000000000L, null)]
    [InlineData(18000000001L, 18000000001L, TimingWindowPolicy.AfterWindow)]
    [InlineData(120000000L, 0, null)]
    [InlineData(120000001L, 0, TimingWindowPolicy.AheadOfClock)]
    public void UsesInclusiveBoundariesAndDistinguishesCaptureFailures(long observedUs, long receivedUs, string? reason)
    {
        Assert.Equal(reason, TimingWindowPolicy.Diagnose(Start, End, Start.AddTicks(observedUs * 10),
            Start.AddTicks(receivedUs * 10)));
    }

    [Fact]
    public void WindowStatusUsesServerInstantAndIncludesBothBoundaries()
    {
        Assert.Equal("Upcoming", TimingWindowPolicy.Status(Start, End, Start.AddTicks(-10)));
        Assert.Equal("Active", TimingWindowPolicy.Status(Start, End, Start));
        Assert.Equal("Active", TimingWindowPolicy.Status(Start, End, End));
        Assert.Equal("Expired", TimingWindowPolicy.Status(Start, End, End.AddTicks(10)));
    }
}
