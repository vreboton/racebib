using RaceTiming.Domain.Enums;

namespace RaceTiming.Domain.Entities;

public sealed class Course
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public int Version { get; set; } = 1;
    public decimal DistanceKm { get; set; }
    public int? ElevationGainMeters { get; set; }
    public RecordStatus Status { get; set; } = RecordStatus.Active;
    public Event Event { get; set; } = null!;
    public ICollection<CourseCheckpoint> Checkpoints { get; set; } = new List<CourseCheckpoint>();
    public ICollection<Race> Races { get; set; } = new List<Race>();
}
