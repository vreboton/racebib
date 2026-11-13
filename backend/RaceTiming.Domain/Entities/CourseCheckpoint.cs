namespace RaceTiming.Domain.Entities;

public sealed class CourseCheckpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public required string Name { get; set; }
    public required string Code { get; set; }
    public int Sequence { get; set; }
    public decimal DistanceFromStartKm { get; set; }
    public int? ElevationMeters { get; set; }
    public bool IsMandatory { get; set; } = true;
    public bool IsStart { get; set; }
    public bool IsFinish { get; set; }
    public bool HasCutoff { get; set; }
    public int? CutoffDurationSeconds { get; set; }
    public string? Description { get; set; }
    public Course Course { get; set; } = null!;
}
