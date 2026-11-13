namespace RaceTiming.Domain.Entities;

public sealed class Organizer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
    public bool IsSuspended { get; set; }
    public ICollection<OrganizerMembership> Memberships { get; set; } = new List<OrganizerMembership>();
    public ICollection<Event> Events { get; set; } = new List<Event>();
}
