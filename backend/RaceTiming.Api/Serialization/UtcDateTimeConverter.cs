using System.Text.Json;
using System.Text.Json.Serialization;

namespace RaceTiming.Api.Serialization;

// MySQL DATETIME returns wall-clock fields without a Kind. RaceBib stores those fields in UTC.
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetDateTime(out var value))
            throw new JsonException("Expected an ISO 8601 timestamp.");

        // Older clients send offsetless UTC values. An explicit offset must be honored.
        return value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : reader.GetDateTimeOffset().UtcDateTime;
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

public sealed class ExplicitOffsetDateTimeConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetDateTime(out var value)
            || value.Kind == DateTimeKind.Unspecified || !reader.TryGetDateTimeOffset(out var instant))
            throw new JsonException("Expected an ISO 8601 timestamp with Z or an explicit offset.");
        return instant.ToUniversalTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime);
}
