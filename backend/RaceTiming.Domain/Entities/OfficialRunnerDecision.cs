namespace RaceTiming.Domain.Entities;

public enum OfficialClassification { Pending, Finished, Dns, Dnf, Dq }

public sealed class OfficialRunnerDecision
{
    public Guid EventId { get; init; }
    public Guid RaceId { get; init; }
    public Guid RegistrationId { get; init; }
    public int Revision { get; init; }
    public OfficialClassification Classification { get; init; }
    public required string ActorUserId { get; init; }
    public required string Reason { get; init; }
    public DateTime DecidedAtUtc { get; init; }
    public required string EvidenceFingerprint { get; init; }
}
