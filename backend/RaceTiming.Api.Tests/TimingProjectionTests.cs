using Microsoft.EntityFrameworkCore.Migrations.Operations;
using RaceTiming.Application;
using RaceTiming.Domain.Entities;
using RaceTiming.Infrastructure.Persistence.Migrations;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed class TimingProjectionTests
{
    [Fact]
    public void ProjectionUsesLatestDecisionAndRejectsConflictingAcceptedSlots()
    {
        var registration = Guid.NewGuid();
        var visit = Guid.NewGuid();
        var first = Observation(registration);
        var second = Observation(registration);
        var firstAccepted = Decision(first, 1, visit, TimingOutcome.Accepted);
        var firstInvalidated = Decision(first, 2, null, TimingOutcome.Invalidated);
        var secondAccepted = Decision(second, 1, visit, TimingOutcome.Accepted);

        var reconstructed = TimingProjection.Reconstruct([second, first], [firstInvalidated, secondAccepted, firstAccepted]);
        var slot = Assert.Single(reconstructed);
        Assert.Equal(second.Id, slot.ObservationId);
        Assert.Equal(visit, slot.OccurrenceId);
        Assert.Throws<InvalidOperationException>(() => TimingProjection.Reconstruct([first, second], [firstAccepted, secondAccepted]));
        Assert.Throws<InvalidOperationException>(() => TimingProjection.Reconstruct([first], []));
    }

    [Fact]
    public void FoundationMigrationAddsTablesAndReferencedKeysWithoutRewritingLegacyRows()
    {
        var operations = new AddTimingFoundation().UpOperations;
        Assert.All(operations, operation => Assert.True(operation is CreateTableOperation or CreateIndexOperation or AddUniqueConstraintOperation));
        var tables = operations.OfType<CreateTableOperation>().ToDictionary(x => x.Name);
        Assert.Contains("TimingObservations", tables.Keys);
        Assert.Contains("AcceptedTimings", tables.Keys);
        Assert.Contains(tables["TimingVisits"].ForeignKeys, x => x.PrincipalTable == "CourseCheckpoints");
        Assert.Contains(tables["TimingRosterEntries"].ForeignKeys, x => x.PrincipalTable == "Registrations");
        Assert.Contains(operations.OfType<CreateIndexOperation>(), x => x.Name == "UX_TimingObservations_Operation" && x.IsUnique);
    }

    private static TimingObservation Observation(Guid registration) => new()
    {
        Id = Guid.NewGuid(), RegistrationId = registration, BibNumber = "42", ActorUserId = "operator"
    };

    private static TimingDecision Decision(TimingObservation evidence, int revision, Guid? occurrence, TimingOutcome outcome) => new()
    {
        ObservationId = evidence.Id, Revision = revision, OccurrenceId = occurrence, Outcome = outcome,
        ActorUserId = "manager", Reason = "Synthetic decision"
    };
}
