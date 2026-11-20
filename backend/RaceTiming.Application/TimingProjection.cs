using RaceTiming.Domain.Entities;

namespace RaceTiming.Application;

public static class TimingProjection
{
    public static AcceptedTiming Accept(TimingObservation observation, TimingDecision decision)
    {
        if (decision.ObservationId != observation.Id || decision.Outcome != TimingOutcome.Accepted ||
            decision.OccurrenceId is not Guid occurrenceId)
            throw new InvalidOperationException("An accepted slot requires an accepted decision for the observation.");

        return new AcceptedTiming
        {
            EventId = observation.EventId, RaceId = observation.RaceId, RegistrationId = observation.RegistrationId,
            OccurrenceId = occurrenceId, ObservationId = observation.Id, DecisionRevision = decision.Revision
        };
    }

    public static IReadOnlyList<AcceptedTiming> Reconstruct(IEnumerable<TimingObservation> observations,
        IEnumerable<TimingDecision> decisions)
    {
        var latest = decisions.GroupBy(x => x.ObservationId).ToDictionary(x => x.Key, x => x.MaxBy(d => d.Revision)!);
        var slots = new Dictionary<(Guid RegistrationId, Guid OccurrenceId), AcceptedTiming>();
        foreach (var observation in observations)
        {
            if (!latest.TryGetValue(observation.Id, out var decision))
                throw new InvalidOperationException("Each observation must have an initial decision.");
            if (decision.Outcome != TimingOutcome.Accepted) continue;
            var accepted = Accept(observation, decision);
            if (!slots.TryAdd((accepted.RegistrationId, accepted.OccurrenceId), accepted))
                throw new InvalidOperationException("The decision history accepts multiple observations for one slot.");
        }
        return slots.Values.OrderBy(x => x.RegistrationId).ThenBy(x => x.OccurrenceId).ToArray();
    }
}
