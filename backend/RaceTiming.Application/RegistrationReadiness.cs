using System.Globalization;
using RaceTiming.Domain.Enums;

namespace RaceTiming.Application;

public sealed record ReadinessCounts(
    int Total = 0, int Confirmed = 0, int Pending = 0, int Waitlisted = 0, int Withdrawn = 0,
    int Historical = 0, int ConfirmedWithoutBib = 0, int BibsAssigned = 0, int ConfirmedCheckedIn = 0,
    int ConfirmedNotCheckedIn = 0, int InactiveRacesWithRegistrations = 0, int InactiveRaceConfirmed = 0,
    int EligibleWithoutBib = 0, int PendingOrWaitlistedEligibleWithoutBib = 0, int InvalidBibs = 0, int DuplicateBibs = 0)
{
    public static ReadinessCounts Sum(IReadOnlyCollection<ReadinessCounts> races) => new(
        races.Sum(x => x.Total), races.Sum(x => x.Confirmed), races.Sum(x => x.Pending),
        races.Sum(x => x.Waitlisted), races.Sum(x => x.Withdrawn), races.Sum(x => x.Historical),
        races.Sum(x => x.ConfirmedWithoutBib), races.Sum(x => x.BibsAssigned), races.Sum(x => x.ConfirmedCheckedIn),
        races.Sum(x => x.ConfirmedNotCheckedIn), races.Sum(x => x.InactiveRacesWithRegistrations),
        races.Sum(x => x.InactiveRaceConfirmed), races.Sum(x => x.EligibleWithoutBib),
        races.Sum(x => x.PendingOrWaitlistedEligibleWithoutBib),
        races.Sum(x => x.InvalidBibs), races.Sum(x => x.DuplicateBibs));

    public ReadinessAssessment Assess(bool expectConfirmed = true)
    {
        var warnings = new List<ReadinessWarning>();
        Add("actionRequired", ConfirmedWithoutBib, "Confirmed registrations without bibs");
        Add("actionRequired", InactiveRaceConfirmed, "Confirmed registrations in inactive races");
        Add("actionRequired", InvalidBibs, "Registrations with invalid bibs");
        Add("actionRequired", DuplicateBibs, "Registrations sharing a bib");
        Add("attention", PendingOrWaitlistedEligibleWithoutBib,
            "Pending or waitlisted registrations eligible for a bib");
        Add("attention", InactiveRacesWithRegistrations, "Inactive races with registrations");
        if (expectConfirmed && Confirmed == 0) warnings.Add(new("attention", null, "No confirmed registrations"));
        var level = warnings.FirstOrDefault()?.Level ?? "ready";
        return new(level, warnings);

        void Add(string level, int count, string label)
        {
            if (count > 0) warnings.Add(new(level, count, label));
        }
    }
}

public sealed record ReadinessWarning(string Level, int? Count, string Label);
public sealed record ReadinessAssessment(string Level, IReadOnlyList<ReadinessWarning> Warnings);

public static class RosterCsv
{
    public const int MaxRows = 5000;

    public static string StatusLabel(RegistrationStatus status) => status switch
    {
        RegistrationStatus.Cancelled => "Withdrawn",
        RegistrationStatus.Dnf => "DNF",
        RegistrationStatus.Dns => "DNS",
        _ => status.ToString()
    };

    public static string SafeField(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        foreach (var character in value)
        {
            if (character is '=' or '+' or '-' or '@' or '\uFF1D' or '\uFF0B' or '\uFF0D' or '\uFF20' or '\t' or '\r' or '\n')
                return "'" + value;
            if (!char.IsWhiteSpace(character) && !char.IsControl(character) && char.GetUnicodeCategory(character) != UnicodeCategory.Format)
                break;
        }
        return value;
    }
}
