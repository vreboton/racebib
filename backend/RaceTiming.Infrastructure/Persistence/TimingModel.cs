using Microsoft.EntityFrameworkCore;
using RaceTiming.Domain.Entities;
using RaceTiming.Infrastructure.Identity;

namespace RaceTiming.Infrastructure.Persistence;

internal static class TimingModel
{
    internal static void Configure(ModelBuilder model)
    {
        var location = model.Entity<PhysicalCheckpoint>().ToTable("PhysicalCheckpoints");
        location.HasAlternateKey(x => new { x.Id, x.EventId });
        location.HasIndex(x => new { x.EventId, x.Code }).IsUnique();
        location.Property(x => x.Code).HasMaxLength(40);
        location.Property(x => x.Name).HasMaxLength(160);
        location.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);

        var mapping = model.Entity<CheckpointMapping>().ToTable("CheckpointMappings");
        mapping.HasKey(x => x.OccurrenceId);
        mapping.HasOne<CourseCheckpoint>().WithMany().HasForeignKey(x => new { x.OccurrenceId, x.CourseId })
            .HasPrincipalKey(x => new { x.Id, x.CourseId }).OnDelete(DeleteBehavior.Cascade);
        mapping.HasOne<Course>().WithMany().HasForeignKey(x => new { x.CourseId, x.EventId })
            .HasPrincipalKey(x => new { x.Id, x.EventId }).OnDelete(DeleteBehavior.Restrict);
        mapping.HasOne<PhysicalCheckpoint>().WithMany().HasForeignKey(x => new { x.CheckpointId, x.EventId })
            .HasPrincipalKey(x => new { x.Id, x.EventId }).OnDelete(DeleteBehavior.Restrict);

        var grant = model.Entity<CheckpointAssignment>().ToTable("CheckpointAssignments");
        grant.HasKey(x => new { x.EventId, x.CheckpointId, x.UserId });
        grant.HasIndex(x => new { x.UserId, x.Active });
        grant.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        grant.HasOne<PhysicalCheckpoint>().WithMany().HasForeignKey(x => new { x.CheckpointId, x.EventId })
            .HasPrincipalKey(x => new { x.Id, x.EventId }).OnDelete(DeleteBehavior.Restrict);

        var setup = model.Entity<RaceTimingSetup>().ToTable("RaceTimingSetups");
        setup.HasKey(x => x.RaceId);
        setup.Property(x => x.TimeZoneId).HasMaxLength(100);
        setup.HasAlternateKey(x => new { x.RaceId, x.EventId });
        setup.HasOne<Race>().WithMany().HasForeignKey(x => new { x.RaceId, x.EventId })
            .HasPrincipalKey(x => new { x.Id, x.EventId }).OnDelete(DeleteBehavior.Restrict);
        setup.HasOne<Course>().WithMany().HasForeignKey(x => new { x.CourseId, x.EventId })
            .HasPrincipalKey(x => new { x.Id, x.EventId }).OnDelete(DeleteBehavior.Restrict);

        var visit = model.Entity<TimingVisit>().ToTable("TimingVisits");
        visit.HasKey(x => new { x.RaceId, x.OccurrenceId });
        visit.HasAlternateKey(x => new { x.RaceId, x.OccurrenceId, x.CheckpointId });
        visit.HasIndex(x => new { x.RaceId, x.Sequence }).IsUnique();
        visit.HasIndex(x => new { x.RaceId, x.CheckpointId });
        visit.Property(x => x.DistanceKm).HasPrecision(8, 3);
        visit.Property(x => x.Name).HasMaxLength(160);
        visit.HasOne<CourseCheckpoint>().WithMany().HasForeignKey(x => x.OccurrenceId).OnDelete(DeleteBehavior.Restrict);
        visit.HasOne<RaceTimingSetup>().WithMany().HasForeignKey(x => new { x.RaceId, x.EventId })
            .HasPrincipalKey(x => new { x.RaceId, x.EventId }).OnDelete(DeleteBehavior.Restrict);
        visit.HasOne<PhysicalCheckpoint>().WithMany().HasForeignKey(x => new { x.CheckpointId, x.EventId })
            .HasPrincipalKey(x => new { x.Id, x.EventId }).OnDelete(DeleteBehavior.Restrict);

        var wave = model.Entity<TimingWave>().ToTable("TimingWaves");
        wave.HasKey(x => new { x.RaceId, x.WaveId });
        wave.HasOne<RaceTimingSetup>().WithMany().HasForeignKey(x => x.RaceId).OnDelete(DeleteBehavior.Restrict);
        wave.HasOne<StartWave>().WithMany().HasForeignKey(x => new { x.WaveId, x.RaceId })
            .HasPrincipalKey(x => new { x.Id, x.RaceId }).OnDelete(DeleteBehavior.Restrict);

        var roster = model.Entity<TimingRosterEntry>().ToTable("TimingRosterEntries");
        roster.HasKey(x => new { x.EventId, x.RegistrationId });
        roster.HasAlternateKey(x => new { x.EventId, x.RegistrationId, x.RaceId });
        roster.HasIndex(x => new { x.EventId, x.BibNumber }).IsUnique();
        roster.Property(x => x.BibNumber).HasMaxLength(32);
        roster.HasOne<Registration>().WithMany().HasForeignKey(x => new { x.RegistrationId, x.EventId })
            .HasPrincipalKey(x => new { x.Id, x.EventId }).OnDelete(DeleteBehavior.Restrict);
        roster.HasOne<RaceTimingSetup>().WithMany().HasForeignKey(x => new { x.RaceId, x.EventId })
            .HasPrincipalKey(x => new { x.RaceId, x.EventId }).OnDelete(DeleteBehavior.Restrict);
        roster.HasOne<TimingWave>().WithMany().HasForeignKey(x => new { x.RaceId, x.WaveId }).OnDelete(DeleteBehavior.Restrict);

        var gun = model.Entity<OfficialGunDeclaration>().ToTable("OfficialGunDeclarations");
        gun.Property(x => x.Reason).HasMaxLength(300);
        gun.HasIndex(x => new { x.RaceId, x.WaveId, x.DeclaredAtUtc });
        gun.HasIndex(x => x.SupersedesId).IsUnique();
        gun.HasOne<RaceTimingSetup>().WithMany().HasForeignKey(x => x.RaceId).OnDelete(DeleteBehavior.Restrict);
        gun.HasOne<TimingWave>().WithMany().HasForeignKey(x => new { x.RaceId, x.WaveId }).OnDelete(DeleteBehavior.Restrict);
        gun.HasOne<OfficialGunDeclaration>().WithMany().HasForeignKey(x => x.SupersedesId).OnDelete(DeleteBehavior.Restrict);
        gun.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);

        var observation = model.Entity<TimingObservation>().ToTable("TimingObservations");
        observation.HasAlternateKey(x => new { x.Id, x.EventId, x.RegistrationId, x.RaceId });
        observation.HasIndex(x => new { x.EventId, x.ActorUserId, x.DeviceId, x.IdempotencyKey }).IsUnique()
            .HasDatabaseName("UX_TimingObservations_Operation");
        observation.HasIndex(x => new { x.EventId, x.CheckpointId, x.ReceivedAtUtc });
        observation.HasIndex(x => new { x.EventId, x.RegistrationId, x.ObservedAtUtc });
        observation.Property(x => x.Id).ValueGeneratedNever();
        observation.Property(x => x.BibNumber).HasMaxLength(32);
        observation.HasOne<TimingRosterEntry>().WithMany().HasForeignKey(x => new { x.EventId, x.RegistrationId, x.RaceId })
            .HasPrincipalKey(x => new { x.EventId, x.RegistrationId, x.RaceId }).OnDelete(DeleteBehavior.Restrict);
        observation.HasOne<TimingVisit>().WithMany().HasForeignKey(x => new { x.RaceId, x.ProposedOccurrenceId, x.CheckpointId })
            .HasPrincipalKey(x => new { x.RaceId, x.OccurrenceId, x.CheckpointId }).OnDelete(DeleteBehavior.Restrict);
        observation.HasOne<CheckpointAssignment>().WithMany().HasForeignKey(x => new { x.EventId, x.CheckpointId, x.ActorUserId })
            .OnDelete(DeleteBehavior.Restrict);

        var decision = model.Entity<TimingDecision>().ToTable("TimingDecisions");
        decision.HasKey(x => new { x.ObservationId, x.Revision });
        decision.Property(x => x.Reason).HasMaxLength(300);
        decision.HasOne<TimingObservation>().WithMany().HasForeignKey(x => x.ObservationId).OnDelete(DeleteBehavior.Restrict);
        decision.HasOne<CourseCheckpoint>().WithMany().HasForeignKey(x => x.OccurrenceId).OnDelete(DeleteBehavior.Restrict);
        decision.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);

        var accepted = model.Entity<AcceptedTiming>().ToTable("AcceptedTimings");
        accepted.HasKey(x => new { x.RegistrationId, x.OccurrenceId });
        accepted.HasIndex(x => x.ObservationId).IsUnique();
        accepted.HasIndex(x => new { x.EventId, x.RaceId, x.OccurrenceId });
        accepted.HasOne<TimingVisit>().WithMany().HasForeignKey(x => new { x.RaceId, x.OccurrenceId }).OnDelete(DeleteBehavior.Restrict);
        accepted.HasOne<TimingObservation>().WithMany().HasForeignKey(x => new { x.ObservationId, x.EventId, x.RegistrationId, x.RaceId })
            .HasPrincipalKey(x => new { x.Id, x.EventId, x.RegistrationId, x.RaceId }).OnDelete(DeleteBehavior.Restrict);
        accepted.HasOne<TimingDecision>().WithMany().HasForeignKey(x => new { x.ObservationId, x.DecisionRevision }).OnDelete(DeleteBehavior.Restrict);
    }
}
