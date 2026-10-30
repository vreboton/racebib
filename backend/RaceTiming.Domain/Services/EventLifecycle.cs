using RaceTiming.Domain.Enums;

namespace RaceTiming.Domain.Services;

public static class EventLifecycle
{
    private static readonly IReadOnlyDictionary<EventStatus, EventStatus[]> Allowed = new Dictionary<EventStatus, EventStatus[]>
    {
        [EventStatus.Draft] = [EventStatus.Published, EventStatus.Cancelled],
        [EventStatus.Published] = [EventStatus.Draft, EventStatus.RegistrationOpen, EventStatus.Cancelled],
        [EventStatus.RegistrationOpen] = [EventStatus.RegistrationClosed, EventStatus.Cancelled],
        [EventStatus.RegistrationClosed] = [EventStatus.RegistrationOpen, EventStatus.Ready, EventStatus.Cancelled],
        [EventStatus.Ready] = [EventStatus.Live, EventStatus.Cancelled],
        [EventStatus.Live] = [EventStatus.Completed, EventStatus.Cancelled],
        [EventStatus.Completed] = [],
        [EventStatus.Cancelled] = []
    };

    public static bool CanTransition(EventStatus current, EventStatus next) =>
        Allowed.TryGetValue(current, out var transitions) && transitions.Contains(next);
}
