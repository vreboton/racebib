using System.Security.Cryptography;
using System.Text.Json;
using RaceTiming.Domain.Entities;

namespace RaceTiming.Application;

public static class OfficialTimingPolicy
{
    public static bool CanClassify(OfficialClassification classification, RegistrationTiming timing, bool bibConflict) =>
        classification != OfficialClassification.Finished || timing.ProvisionalFinish is not null && !bibConflict;

    public static string Fingerprint(RegistrationTiming timing, bool bibConflict)
    {
        // Version the canonical evidence input independently of the result DTO serialization.
        var evidence = new
        {
            version = 1, bibConflict, timing.GunDeclarationId,
            visits = timing.Visits.OrderBy(x => x.Sequence).Select(x => new
            {
                x.OccurrenceId, x.ObservationId, x.DecisionRevision, crossing = x.CrossingAtUtc?.UtcTicks
            })
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(evidence)));
    }
}
