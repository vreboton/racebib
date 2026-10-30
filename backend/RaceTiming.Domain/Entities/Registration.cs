using RaceTiming.Domain.Enums;

namespace RaceTiming.Domain.Entities;

public sealed class Registration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public Guid RunnerId { get; set; }
    public Guid RaceCategoryId { get; set; }
    public Guid CourseId { get; set; }
    public required string BibNumber { get; set; }
    public RegistrationStatus RegistrationStatus { get; set; } = RegistrationStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Event Event { get; set; } = null!;
    public Runner Runner { get; set; } = null!;
    public RaceCategory RaceCategory { get; set; } = null!;
    public Course Course { get; set; } = null!;
}
