namespace RaceTiming.Domain.Services;

public static class TimingWindowPolicy
{
    public const string BeforeWindow = "observed_before_timing_window";
    public const string AfterWindow = "observed_after_timing_window";
    public const string AheadOfClock = "observed_ahead_of_server_clock";
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(2);

    public static bool Contains(DateTime start, DateTime end, DateTime instant) => instant >= start && instant <= end;

    public static string Status(DateTime start, DateTime end, DateTime now) =>
        now < start ? "Upcoming" : now > end ? "Expired" : "Active";

    public static string? Diagnose(DateTime start, DateTime end, DateTime observed, DateTime received) =>
        observed < start ? BeforeWindow : observed > end ? AfterWindow :
        observed > received.Add(FutureTolerance) ? AheadOfClock : null;
}
