using RaceTiming.Domain.Enums;

namespace RaceTiming.Domain.Entities;

public sealed class RaceCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public Guid? CourseId { get; set; }
    public required string Name { get; set; }
    public decimal DistanceKm { get; set; }
    public string? Description { get; set; }
    public int? CutoffDurationSeconds { get; set; }
    public TimeOnly? StartTime { get; set; }
    public RecordStatus Status { get; set; } = RecordStatus.Active;
    public Event Event { get; set; } = null!;
    public Course? Course { get; set; }
    public ICollection<Registration> Registrations { get; set; } = new List<Registration>();
}
