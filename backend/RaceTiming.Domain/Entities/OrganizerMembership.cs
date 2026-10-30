using RaceTiming.Domain.Enums;

namespace RaceTiming.Domain.Entities;

public sealed class OrganizerMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizerId { get; set; }
    public required string UserId { get; set; }
    public OrganizerRole Role { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
    public Organizer Organizer { get; set; } = null!;
}
