namespace RaceTiming.Domain.Entities;

public sealed class Runner
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizerId { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public string? PublicDisplayName { get; set; }
    public string? Email { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Organizer Organizer { get; set; } = null!;
    public ICollection<Registration> Registrations { get; set; } = new List<Registration>();
}
