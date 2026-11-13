using RaceTiming.Domain.Enums;

namespace RaceTiming.Domain.Entities;

public sealed class Race
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public Guid CourseId { get; set; }
    public required string Name { get; set; }
    public decimal DistanceKm { get; set; }
    public string? Description { get; set; }
    public DateTime StartAtUtc { get; set; }
    public int? CutoffDurationSeconds { get; set; }
    public RecordStatus Status { get; set; } = RecordStatus.Active;
    public Event Event { get; set; } = null!;
    public Course Course { get; set; } = null!;
    public ICollection<RaceCategory> Categories { get; set; } = new List<RaceCategory>();
    public ICollection<StartWave> StartWaves { get; set; } = new List<StartWave>();
    public ICollection<Registration> Registrations { get; set; } = new List<Registration>();
}
