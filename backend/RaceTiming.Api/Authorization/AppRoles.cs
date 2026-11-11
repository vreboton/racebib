namespace RaceTiming.Api.Authorization;

public static class AppRoles
{
    public const string SystemAdministrator = "SystemAdministrator";
    public const string EventAdministrator = "EventAdministrator";
    public const string RaceDirector = "RaceDirector";
    public const string TimingOfficial = "TimingOfficial";
    public const string CheckpointMarshal = "CheckpointMarshal";
    public const string SafetyOfficer = "SafetyOfficer";

    // Organizer responsibilities are membership roles, never global Identity roles.
    public static readonly string[] All = [SystemAdministrator];
}
