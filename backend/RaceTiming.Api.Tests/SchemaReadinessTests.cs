using RaceTiming.Api.Infrastructure;
using Xunit;

namespace RaceTiming.Api.Tests;

public class SchemaReadinessTests
{
    [Fact]
    public void ExactHistoryIsRequiredForRollbackAndDeployment()
    {
        SchemaReadiness.RequireHistory(["001", "002"], ["001", "002"]);
        Assert.Throws<InvalidOperationException>(() => SchemaReadiness.RequireHistory(["001", "002"], []));
        Assert.Throws<InvalidOperationException>(() => SchemaReadiness.RequireHistory(["001", "002"], ["001"]));
        Assert.Throws<InvalidOperationException>(() => SchemaReadiness.RequireHistory(["001"], ["001", "002"]));
        Assert.Throws<InvalidOperationException>(() => SchemaReadiness.RequireHistory([], []));
    }

    [Fact]
    public void MatchingHistoryCannotHideChangedStructure()
    {
        SchemaReadiness.RequireHistory(["001"], ["001"]);
        SchemaReadiness.RequireContract("expected structure", "expected structure");
        Assert.Throws<InvalidOperationException>(() => SchemaReadiness.RequireContract("expected structure", "missing column"));
    }
}
