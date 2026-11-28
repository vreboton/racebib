namespace RaceTiming.Domain.Entities;

public enum TimingBasis { Gun, Net }
public enum TimingSource { Manual, Scanner }
public enum TimingOutcome { Accepted, AmbiguousOccurrence, SlotConflict, SuspiciousClock, SequenceConflict, Invalidated }

public sealed class PhysicalCheckpoint
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid EventId { get; init; }
    public required string Name { get; init; }
    public required string Code { get; init; }
}

public sealed class CheckpointAssignment
{
    public Guid EventId { get; init; }
    public Guid CheckpointId { get; init; }
    public required string UserId { get; init; }
    public Guid Generation { get; set; }
    public bool Active { get; set; }
    public int OfflineHours { get; set; } = 12;
    public Guid OfflinePolicyRevision { get; set; }
}

public sealed class RaceTimingSetup
{
    public Guid RaceId { get; init; }
    public Guid EventId { get; init; }
    public Guid CourseId { get; init; }
    public DateTime ArmedAtUtc { get; init; }
    public DateTime WindowStartUtc { get; init; }
    public DateTime WindowEndUtc { get; init; }
    public required string TimeZoneId { get; init; }
    public DateTime ScheduledGunUtc { get; init; }
    public int? CutoffDurationSeconds { get; init; }
    public TimingBasis ResultsBasis { get; init; }
    public TimingBasis CutoffBasis { get; init; }
}

public sealed class TimingVisit
{
    public Guid RaceId { get; init; }
    public Guid OccurrenceId { get; init; }
    public Guid EventId { get; init; }
    public Guid CheckpointId { get; init; }
    public int Sequence { get; init; }
    public decimal DistanceKm { get; init; }
    public required string Name { get; init; }
    public bool IsStart { get; init; }
    public bool IsFinish { get; init; }
    public bool IsMandatory { get; init; }
    public int? CutoffDurationSeconds { get; init; }
}

public sealed class TimingWave
{
    public Guid RaceId { get; init; }
    public Guid WaveId { get; init; }
    public DateTime ScheduledGunUtc { get; init; }
}

public sealed class TimingRosterEntry
{
    public Guid EventId { get; init; }
    public Guid RegistrationId { get; init; }
    public Guid RaceId { get; init; }
    public Guid? WaveId { get; init; }
    public required string BibNumber { get; init; }
}

public sealed class OfficialGunDeclaration
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid RaceId { get; init; }
    public Guid? WaveId { get; init; }
    public DateTime GunAtUtc { get; init; }
    public DateTime DeclaredAtUtc { get; init; }
    public required string ActorUserId { get; init; }
    public required string Reason { get; init; }
    public Guid? SupersedesId { get; init; }
}

public sealed class TimingObservation
{
    public Guid Id { get; init; }
    public Guid EventId { get; init; }
    public Guid RaceId { get; init; }
    public Guid RegistrationId { get; init; }
    public Guid CheckpointId { get; init; }
    public Guid? ProposedOccurrenceId { get; init; }
    public required string BibNumber { get; init; }
    public DateTime ObservedAtUtc { get; init; }
    public DateTime ReceivedAtUtc { get; init; }
    public required string ActorUserId { get; init; }
    public Guid DeviceId { get; init; }
    public Guid IdempotencyKey { get; init; }
    public Guid AssignmentGeneration { get; init; }
    public TimingSource Source { get; init; }
}

public sealed class TimingDecision
{
    public Guid ObservationId { get; init; }
    public int Revision { get; init; }
    public Guid? OccurrenceId { get; init; }
    public TimingOutcome Outcome { get; init; }
    public DateTime DecidedAtUtc { get; init; }
    public required string ActorUserId { get; init; }
    public required string Reason { get; init; }
}

public sealed class AcceptedTiming
{
    public Guid RegistrationId { get; init; }
    public Guid OccurrenceId { get; init; }
    public Guid EventId { get; init; }
    public Guid RaceId { get; init; }
    public Guid ObservationId { get; init; }
    public int DecisionRevision { get; init; }
}


public sealed class CheckpointMapping
{
    public Guid OccurrenceId { get; init; }
    public Guid CourseId { get; init; }
    public Guid EventId { get; init; }
    public Guid CheckpointId { get; set; }
}
