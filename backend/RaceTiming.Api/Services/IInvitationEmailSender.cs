namespace RaceTiming.Api.Services;

public interface IInvitationEmailSender
{
    Task SendAsync(string email, string organizerName, string acceptanceUrl, DateTime expiresAtUtc, CancellationToken cancellationToken);
}
