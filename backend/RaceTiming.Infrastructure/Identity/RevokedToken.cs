namespace RaceTiming.Infrastructure.Identity;

public sealed class RevokedToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string JwtId { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime RevokedAtUtc { get; set; } = DateTime.UtcNow;
}
