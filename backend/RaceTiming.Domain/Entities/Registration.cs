using RaceTiming.Domain.Enums;

namespace RaceTiming.Domain.Entities;

public sealed class Registration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public Guid OrganizerId { get; set; }
    public Guid RaceId { get; set; }
    public Guid RunnerId { get; set; }
    public Guid? RaceCategoryId { get; set; }
    public Guid? StartWaveId { get; set; }
    public string? BibNumber { get; set; }
    public string? BibDisplayName { get; set; }
    public PaymentStatus PaymentStatus { get; set; }
    public string? Team { get; set; }

    public string EffectiveBibDisplayName => DisplayName(BibDisplayName, Runner.FirstName, Runner.LastName);

    public static string DisplayName(string? bibDisplayName, string firstName, string lastName) =>
        string.IsNullOrWhiteSpace(bibDisplayName)
        ? $"{firstName} {lastName}" : bibDisplayName.Trim();
    public long Version { get; set; } = 1;
    public RegistrationStatus RegistrationStatus { get; set; } = RegistrationStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Event Event { get; set; } = null!;
    public Race Race { get; set; } = null!;
    public Runner Runner { get; set; } = null!;
    public RaceCategory? RaceCategory { get; set; }
    public StartWave? StartWave { get; set; }
}
