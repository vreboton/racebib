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
    public DbSet<OrganizerInvitation> OrganizerInvitations => Set<OrganizerInvitation>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Race> Races => Set<Race>();
    public DbSet<RaceCategory> RaceCategories => Set<RaceCategory>();
    public DbSet<StartWave> StartWaves => Set<StartWave>();
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
        b.Entity<OrganizerInvitation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(254).IsRequired();
            e.Property(x => x.NormalizedEmail).HasMaxLength(254).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.CreatedByUserId).HasMaxLength(255).UseCollation("utf8mb4_0900_ai_ci").IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired().IsConcurrencyToken();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.ActiveInvitationKey).HasMaxLength(300);
            e.HasIndex(x => x.ActiveInvitationKey).IsUnique();
            e.HasIndex(x => new { x.OrganizerId, x.CreatedAtUtc });
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.CreatedByUserId);
            e.HasOne(x => x.Organizer).WithMany().HasForeignKey(x => x.OrganizerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AuditRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ActorIdentity).HasMaxLength(254);
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.TargetEntityType).HasMaxLength(80).IsRequired();
            e.Property(x => x.TargetEntityId).HasMaxLength(80).IsRequired();
            e.Property(x => x.MetadataJson).HasColumnType("json");
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => new { x.ActorUserId, x.TimestampUtc });
            e.HasIndex(x => new { x.Action, x.TimestampUtc });
            e.HasIndex(x => new { x.OrganizerId, x.TimestampUtc });
            e.HasOne<Organizer>().WithMany().HasForeignKey(x => x.OrganizerId).OnDelete(DeleteBehavior.SetNull);
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
            e.HasMany(x => x.Races).WithOne(x => x.Event).HasForeignKey(x => x.EventId);
            e.HasMany(x => x.Courses).WithOne(x => x.Event).HasForeignKey(x => x.EventId);
            e.HasAlternateKey(x => new { x.Id, x.OrganizerId });
            e.HasMany(x => x.Registrations).WithOne(x => x.Event).HasForeignKey(x => new { x.EventId, x.OrganizerId }).HasPrincipalKey(x => new { x.Id, x.OrganizerId });
        });
        b.Entity<Course>(e =>
        {
            e.HasIndex(x => new { x.EventId, x.Name, x.Version }).IsUnique();
            e.Property(x => x.DistanceKm).HasPrecision(8, 3);
            e.HasAlternateKey(x => new { x.Id, x.EventId });
        });
        b.Entity<Race>(e =>
        {
            e.HasIndex(x => new { x.EventId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.DistanceKm).HasPrecision(8, 3);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.HasAlternateKey(x => new { x.Id, x.EventId });
            e.HasOne(x => x.Course).WithMany(x => x.Races)
                .HasForeignKey(x => new { x.CourseId, x.EventId })
                .HasPrincipalKey(x => new { x.Id, x.EventId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<RaceCategory>(e =>
        {
            e.HasIndex(x => new { x.RaceId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.HasAlternateKey(x => new { x.Id, x.RaceId });
            e.HasOne(x => x.Race).WithMany(x => x.Categories).HasForeignKey(x => x.RaceId);
            e.ToTable(t => t.HasCheckConstraint("CK_RaceCategory_AgeRange",
                "`MinimumAge` IS NULL OR `MaximumAge` IS NULL OR `MaximumAge` >= `MinimumAge`"));
        });
        b.Entity<StartWave>(e =>
        {
            e.HasIndex(x => new { x.RaceId, x.Sequence }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.HasAlternateKey(x => new { x.Id, x.RaceId });
            e.HasOne(x => x.Race).WithMany(x => x.StartWaves).HasForeignKey(x => x.RaceId);
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
            e.Property(x => x.Gender).HasMaxLength(40);
            e.Property(x => x.MobileNumber).HasMaxLength(40);
            e.Property(x => x.EmergencyContact).HasMaxLength(250);
            e.HasAlternateKey(x => new { x.Id, x.OrganizerId });
            e.HasOne(x => x.Organizer).WithMany().HasForeignKey(x => x.OrganizerId);
            e.HasIndex(x => new { x.OrganizerId, x.Email });
        });
        b.Entity<Registration>(e =>
        {
            e.HasIndex(x => new { x.EventId, x.BibNumber }).IsUnique();
            // The migration reserves NULL solely for grandfathered duplicates.
            e.Property<bool?>("EventRunnerUniquenessSlot").HasDefaultValue(true);
            e.HasIndex(nameof(Registration.EventId), nameof(Registration.RunnerId), "EventRunnerUniquenessSlot")
                .IsUnique().HasDatabaseName("UX_Registrations_Event_Runner_Slot");
            e.ToTable(table => table.HasCheckConstraint("CK_Registrations_EventRunnerSlot",
                "EventRunnerUniquenessSlot IS NULL OR EventRunnerUniquenessSlot = 1"));
            e.Property(x => x.BibNumber).HasMaxLength(32);
            e.Property(x => x.BibDisplayName).HasMaxLength(120);
            e.Property(x => x.Team).HasMaxLength(100);
            e.Property(x => x.PaymentStatus).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.RegistrationStatus).HasConversion<string>().HasMaxLength(24);
            e.HasOne(x => x.Race).WithMany(x => x.Registrations)
                .HasForeignKey(x => new { x.RaceId, x.EventId })
                .HasPrincipalKey(x => new { x.Id, x.EventId });
            e.HasOne(x => x.RaceCategory).WithMany(x => x.Registrations)
                .HasForeignKey(x => new { x.RaceCategoryId, x.RaceId })
                .HasPrincipalKey(x => new { x.Id, x.RaceId }).IsRequired(false);
            e.HasOne(x => x.StartWave).WithMany(x => x.Registrations)
                .HasForeignKey(x => new { x.StartWaveId, x.RaceId })
                .HasPrincipalKey(x => new { x.Id, x.RaceId }).IsRequired(false);
            e.HasOne(x => x.Runner).WithMany(x => x.Registrations)
                .HasForeignKey(x => new { x.RunnerId, x.OrganizerId })
                .HasPrincipalKey(x => new { x.Id, x.OrganizerId });
        });
    }
}
