namespace RaceTiming.Domain.Entities;

public sealed class Runner
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public string? PublicDisplayName { get; set; }
    public string? Email { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<Registration> Registrations { get; set; } = new List<Registration>();
}
