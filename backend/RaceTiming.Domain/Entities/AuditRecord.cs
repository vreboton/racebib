namespace RaceTiming.Domain.Entities;

public sealed class AuditRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? ActorUserId { get; set; }
    public string? ActorIdentity { get; set; }
    public required string Action { get; set; }
    public required string TargetEntityType { get; set; }
    public required string TargetEntityId { get; set; }
    public Guid? OrganizerId { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string? MetadataJson { get; set; }
}
