using RaceTiming.Domain.Enums;

namespace RaceTiming.Domain.Entities;

public sealed class Event
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizerId { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? Description { get; set; }
    public required string OrganizerName { get; set; }
    public string? Venue { get; set; }
    public string? Location { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public required string TimeZoneId { get; set; }
    public EventStatus Status { get; set; } = EventStatus.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Organizer Organizer { get; set; } = null!;
    public ICollection<Race> Races { get; set; } = new List<Race>();
    public ICollection<Course> Courses { get; set; } = new List<Course>();
    public ICollection<Registration> Registrations { get; set; } = new List<Registration>();
}
