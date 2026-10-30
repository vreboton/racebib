using RaceTiming.Domain.Enums;

namespace RaceTiming.Domain.Entities;

public sealed class StartWave
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RaceId { get; set; }
    public required string Name { get; set; }
    public int Sequence { get; set; }
    public DateTime StartAtUtc { get; set; }
    public int? Capacity { get; set; }
    public RecordStatus Status { get; set; } = RecordStatus.Active;
    public Race Race { get; set; } = null!;
    public ICollection<Registration> Registrations { get; set; } = new List<Registration>();
}
