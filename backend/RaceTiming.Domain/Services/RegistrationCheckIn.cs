using RaceTiming.Domain.Enums;

namespace RaceTiming.Domain.Services;

public static class RegistrationCheckIn
{
    public static string? GetIneligibility(EventStatus status, RegistrationStatus registration, RecordStatus race, string? bib)
    {
        if (status is not (EventStatus.RegistrationOpen or EventStatus.RegistrationClosed or EventStatus.Ready or EventStatus.Live))
            return "Check-in is available during Registration Open, Registration Closed, Ready, or Live.";
        if (registration != RegistrationStatus.Confirmed) return "Only confirmed runners can check in.";
        if (race != RecordStatus.Active) return "The runner's race is inactive.";
        return string.IsNullOrWhiteSpace(bib) ? "Assign a bib before checking in." : null;
    }
}
