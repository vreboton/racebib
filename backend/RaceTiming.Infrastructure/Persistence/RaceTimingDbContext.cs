using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Domain.Entities;
using RaceTiming.Infrastructure.Identity;

namespace RaceTiming.Infrastructure.Persistence;

public sealed class RaceTimingDbContext(DbContextOptions<RaceTimingDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<RevokedToken> RevokedTokens => Set<RevokedToken>();
    public DbSet<Organizer> Organizers => Set<Organizer>();
    public DbSet<OrganizerMembership> OrganizerMemberships => Set<OrganizerMembership>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<RaceCategory> RaceCategories => Set<RaceCategory>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<CourseCheckpoint> CourseCheckpoints => Set<CourseCheckpoint>();
    public DbSet<Runner> Runners => Set<Runner>();
    public DbSet<Registration> Registrations => Set<Registration>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<RevokedToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.JwtId).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.JwtId).IsUnique();
            e.HasIndex(x => x.ExpiresAtUtc);
        });

        b.Entity<Organizer>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(160).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(180).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
        });
        b.Entity<OrganizerMembership>(e =>
        {
            e.HasIndex(x => new { x.OrganizerId, x.UserId }).IsUnique();
            e.HasOne(x => x.Organizer).WithMany(x => x.Memberships).HasForeignKey(x => x.OrganizerId);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Event>(e =>
        {
            e.HasIndex(x => new { x.OrganizerId, x.Slug }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(220).IsRequired();
            e.Property(x => x.OrganizerName).HasMaxLength(160).IsRequired();
            e.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.HasOne(x => x.Organizer).WithMany(x => x.Events).HasForeignKey(x => x.OrganizerId);
            e.HasMany(x => x.Categories).WithOne(x => x.Event).HasForeignKey(x => x.EventId);
            e.HasMany(x => x.Courses).WithOne(x => x.Event).HasForeignKey(x => x.EventId);
            e.HasMany(x => x.Registrations).WithOne(x => x.Event).HasForeignKey(x => x.EventId);
        });
        b.Entity<Course>(e =>
        {
            e.HasIndex(x => new { x.EventId, x.Name, x.Version }).IsUnique();
            e.Property(x => x.DistanceKm).HasPrecision(8, 3);
            e.HasAlternateKey(x => new { x.Id, x.EventId });
        });
        b.Entity<RaceCategory>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.DistanceKm).HasPrecision(8, 3);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.HasAlternateKey(x => new { x.Id, x.EventId });
            e.HasOne(x => x.Course).WithMany(x => x.Categories)
                .HasForeignKey(x => new { x.CourseId, x.EventId })
                .HasPrincipalKey(x => new { x.Id, x.EventId }).IsRequired(false);
        });
        b.Entity<CourseCheckpoint>(e =>
        {
            e.HasIndex(x => new { x.CourseId, x.Sequence }).IsUnique();
            e.HasIndex(x => new { x.CourseId, x.Code }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(160).IsRequired();
            e.Property(x => x.Code).HasMaxLength(40).IsRequired();
            e.Property(x => x.DistanceFromStartKm).HasPrecision(8, 3);
            e.HasOne(x => x.Course).WithMany(x => x.Checkpoints).HasForeignKey(x => x.CourseId);
        });
        b.Entity<Runner>(e =>
        {
            e.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
            e.Property(x => x.LastName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Email).HasMaxLength(254);
            e.HasIndex(x => x.Email);
        });
        b.Entity<Registration>(e =>
        {
            e.HasIndex(x => new { x.EventId, x.BibNumber }).IsUnique();
            e.Property(x => x.BibNumber).HasMaxLength(32).IsRequired();
            e.Property(x => x.RegistrationStatus).HasConversion<string>().HasMaxLength(24);
            e.HasOne(x => x.RaceCategory).WithMany(x => x.Registrations)
                .HasForeignKey(x => new { x.RaceCategoryId, x.EventId })
                .HasPrincipalKey(x => new { x.Id, x.EventId });
            e.HasOne(x => x.Course).WithMany(x => x.Registrations)
                .HasForeignKey(x => new { x.CourseId, x.EventId })
                .HasPrincipalKey(x => new { x.Id, x.EventId });
            e.HasOne(x => x.Runner).WithMany(x => x.Registrations).HasForeignKey(x => x.RunnerId);
        });
    }
}
