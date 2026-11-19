using RaceTiming.Application;
using RaceTiming.Domain.Entities;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed class BibAllocatorTests
{
    [Fact]
    public void MixedReservesSuppliedBibsBeforeAllocatingAndKeepsLeadingZeros()
    {
        var error = BibAllocator.Allocate(new(BibAssignmentMode.Mixed, 1, 5, 3), ["002"],
            [null, "001", null, "Trail-7"], out var assigned);

        Assert.Null(error);
        Assert.Equal(new[] { "003", "001", "004", "Trail-7" }, assigned);
    }

    [Fact]
    public void ExistingLeavesBlankBibsUnassignedAndRejectsCaseInsensitiveDuplicates()
    {
        Assert.Null(BibAllocator.Allocate(new(), [], ["0007", null], out var assigned));
        Assert.Equal(new string?[] { "0007", null }, assigned);
        Assert.NotNull(BibAllocator.Allocate(new(), ["X7"], ["x7"], out _));
    }

    [Fact]
    public void AutomaticRejectsSuppliedBibsAndExhaustedRanges()
    {
        var options = new BibAssignment(BibAssignmentMode.Automatic, 1, 2, 2);
        Assert.NotNull(BibAllocator.Allocate(options, [], ["01"], out _));
        Assert.NotNull(BibAllocator.Allocate(options, ["02"], [null, null], out _));
        Assert.Null(BibAllocator.Allocate(options, ["01"], [null], out var assigned));
        Assert.Equal("02", Assert.Single(assigned));
    }

    [Fact]
    public void DisplayNameIsEventSpecificAndWhitespaceFallsBack()
    {
        var runner = new Runner { FirstName = "María", LastName = "Dalisay" };
        var named = new Registration { Runner = runner, BibDisplayName = "小光 🏃" };
        var blank = new Registration { Runner = runner, BibDisplayName = "  " };
        Assert.Equal("小光 🏃", named.EffectiveBibDisplayName);
        Assert.Equal("María Dalisay", blank.EffectiveBibDisplayName);
    }
}
