using RaceTiming.Domain.Enums;

namespace RaceTiming.Application;

public static class BulkBibEligibility
{
    public static string? SkipReason(RegistrationStatus status, RecordStatus race, string? bib, bool checkedIn) =>
        checkedIn ? "Already checked in." :
        status == RegistrationStatus.Cancelled ? "Withdrawn." :
        status is not (RegistrationStatus.Pending or RegistrationStatus.Confirmed or RegistrationStatus.Waitlisted) ? "Historical status." :
        race != RecordStatus.Active ? "Race is inactive." :
        bib is not null ? "Bib already assigned." : null;
}
