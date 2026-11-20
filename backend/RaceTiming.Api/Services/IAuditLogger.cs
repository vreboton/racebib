using System.Security.Claims;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;

namespace RaceTiming.Api.Services;

public interface IAuditLogger
{
    void Record(ClaimsPrincipal? actor, string action, string targetType, string targetId,
        Guid? organizerId = null, IReadOnlyDictionary<string, object?>? metadata = null);
}

public sealed class AuditLogger(RaceTiming.Infrastructure.Persistence.RaceTimingDbContext db) : IAuditLogger
{
    private static readonly HashSet<string> SafeMetadataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "role", "from", "to"
    };
    private static readonly HashSet<string> SafeMetadataValues = new(
        Enum.GetNames<OrganizerRole>().Concat(["Active", "Suspended", "Inactive"]), StringComparer.OrdinalIgnoreCase);

    internal const int MaxReversalReasonLength = 300;
    internal const string BulkBibAssigned = "Registration.BibAssigned";
    internal const string CheckInReversed = "Registration.CheckInReversed";
    internal static bool IsCheckInReversalReason(string? reason) =>
        reason is { Length: > 0 and <= MaxReversalReasonLength } &&
        !string.IsNullOrWhiteSpace(reason) && !reason.Any(char.IsControl);

    public void Record(ClaimsPrincipal? actor, string action, string targetType, string targetId,
        Guid? organizerId = null, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        db.AuditRecords.Add(new AuditRecord
        {
            ActorUserId = actor?.FindFirstValue(ClaimTypes.NameIdentifier),
            ActorIdentity = actor?.FindFirstValue(ClaimTypes.Email) ?? actor?.Identity?.Name,
            Action = action,
            TargetEntityType = targetType,
            TargetEntityId = targetId,
            OrganizerId = organizerId,
            TimestampUtc = DateTime.UtcNow,
            MetadataJson = metadata is null ? null : System.Text.Json.JsonSerializer.Serialize(metadata
                .Where(pair => pair.Value is string value && !IsSensitiveKey(pair.Key) &&
                    ((SafeMetadataKeys.Contains(pair.Key) && SafeMetadataValues.Contains(value)) ||
                     ((action == CheckInReversed && targetType == "Registration" ||
                       action == "Timing.RosterAmended" && targetType == nameof(TimingRosterEntry)) && pair.Key == "reason" &&
                      IsCheckInReversalReason(value)) ||
                     (action == BulkBibAssigned && targetType == "Registration" && pair.Key == "eventId" &&
                      Guid.TryParseExact(value, "D", out _))))
                .ToDictionary(pair => pair.Key, pair => pair.Value))
        });
    }

    private static bool IsSensitiveKey(string key) =>
        key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("jwt", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("refresh", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("signingkey", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("privatekey", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("authorization", StringComparison.OrdinalIgnoreCase);
}
