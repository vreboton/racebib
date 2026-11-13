using System.Net;
using System.Net.Mail;

namespace RaceTiming.Api.Services;

public sealed class SmtpInvitationEmailSender(IConfiguration configuration, IHostEnvironment environment) : IInvitationEmailSender
{
    public async Task SendAsync(string email, string organizerName, string acceptanceUrl, DateTime expiresAtUtc, CancellationToken cancellationToken)
    {
        var host = configuration["Email:Smtp:Host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("SMTP is not configured. Invitation delivery is unavailable.");
            // Local-only delivery: the caller returns the URL in the development response.
            return;
        }

        var from = configuration["Email:Smtp:From"];
        if (string.IsNullOrWhiteSpace(from)) throw new InvalidOperationException("Email:Smtp:From is required when SMTP is configured.");
        using var message = new MailMessage(from, email)
        {
            Subject = $"Invitation to {organizerName} on RaceBib",
            Body = $"You have been invited to join {organizerName} on RaceBib. Accept the invitation before {expiresAtUtc:O}:\n\n{acceptanceUrl}",
            IsBodyHtml = false
        };
        using var client = new SmtpClient(host, configuration.GetValue<int>("Email:Smtp:Port", 587))
        {
            EnableSsl = configuration.GetValue("Email:Smtp:EnableSsl", true),
            Credentials = string.IsNullOrEmpty(configuration["Email:Smtp:Username"])
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(configuration["Email:Smtp:Username"], configuration["Email:Smtp:Password"])
        };
        cancellationToken.ThrowIfCancellationRequested();
        await client.SendMailAsync(message, cancellationToken);
    }
}
