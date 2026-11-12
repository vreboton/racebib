namespace RaceTiming.Domain.Entities;

public enum OrganizerInvitationStatus { Pending = 0, Accepted = 1, Rejected = 2, Revoked = 3, Expired = 4 }

public sealed class OrganizerInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizerId { get; set; }
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public RaceTiming.Domain.Enums.OrganizerRole Role { get; set; }
    public required string TokenHash { get; set; }
    public OrganizerInvitationStatus Status { get; set; } = OrganizerInvitationStatus.Pending;
    public DateTime ExpiresAtUtc { get; set; }
    public required string CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AcceptedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? ActiveInvitationKey { get; set; }
    public Organizer Organizer { get; set; } = null!;
}
