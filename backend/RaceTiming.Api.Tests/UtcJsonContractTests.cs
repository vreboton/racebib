using System.Text.Json;
using RaceTiming.Api.Serialization;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed class UtcJsonContractTests
{
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new UtcDateTimeConverter());
        options.Converters.Add(new ExplicitOffsetDateTimeConverter());
        return options;
    }

    [Fact]
    public void CalendarDatesAndScheduleWallTimesKeepTheirOriginalJsonContracts()
    {
        var options = Options();
        var input = new CalendarFields(new DateOnly(1999, 11, 27), new TimeOnly(12, 30), "2026-11-01T01:30");
        var json = JsonSerializer.Serialize(input, options);
        Assert.Equal("{\"birthdate\":\"1999-11-27\",\"wallTime\":\"12:30:00\",\"scheduleInput\":\"2026-11-01T01:30\"}", json);
        Assert.Equal(input, JsonSerializer.Deserialize<CalendarFields>(json, options));
    }

    [Theory]
    [InlineData("2026-11-27T04:00:00.123456")]
    [InlineData("2026-11-27T12:00:00.123456+08:00")]
    public void EventInstantsAcceptLegacyUtcAndExplicitOffsets(string value)
    {
        var options = Options();
        var parsed = JsonSerializer.Deserialize<DateTime>(JsonSerializer.Serialize(value), options);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.Equal("\"2026-11-27T04:00:00.123456Z\"", JsonSerializer.Serialize(parsed, options));
    }

    [Fact]
    public void TimingEvidenceRequiresAnOffsetAndPreservesItsInstantAndPrecision()
    {
        var options = Options();
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DateTimeOffset>("\"2026-11-27T12:00:00\"", options));
        var instant = JsonSerializer.Deserialize<DateTimeOffset>("\"2026-11-27T12:00:00.123456+08:00\"", options);
        Assert.Equal(TimeSpan.Zero, instant.Offset);
        Assert.Equal("\"2026-11-27T04:00:00.123456Z\"", JsonSerializer.Serialize(instant, options));
    }

    private sealed record CalendarFields(DateOnly Birthdate, TimeOnly WallTime, string ScheduleInput);
}
