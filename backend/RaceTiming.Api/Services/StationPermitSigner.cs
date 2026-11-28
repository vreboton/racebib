using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace RaceTiming.Api.Services;

public sealed record StationPermitClaims(Guid Id, string OperatorId, Guid EventId, Guid CheckpointId,
    Guid Generation, Guid PolicyRevision, long IssuedAt, long ExpiresAt);

public sealed class StationPermitSigner(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public object? Sign(StationPermitClaims claims)
    {
        var key = configuration["OfflineStationPermits:SigningKeyPem"];
        if (string.IsNullOrWhiteSpace(key)) return null;

        using var signer = ECDsa.Create();
        signer.ImportFromPem(key);
        if (signer.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value) throw new InvalidOperationException("Station permits require a P-256 signing key.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(claims, Json);
        var signature = signer.SignData(payload, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new
        {
            payload = WebEncoders.Base64UrlEncode(payload),
            signature = WebEncoders.Base64UrlEncode(signature)
        };
    }
}
