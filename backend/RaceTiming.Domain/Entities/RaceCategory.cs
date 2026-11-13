using RaceTiming.Domain.Enums;

namespace RaceTiming.Domain.Entities;

public sealed class RaceCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RaceId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public int? MinimumAge { get; set; }
    public int? MaximumAge { get; set; }
    public RecordStatus Status { get; set; } = RecordStatus.Active;
    public Race Race { get; set; } = null!;
    public ICollection<Registration> Registrations { get; set; } = new List<Registration>();
}
