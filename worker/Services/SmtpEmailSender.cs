using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace AzCostPilot.Worker.Services;

public sealed class SmtpEmailSender(
    IOptions<NotificationOptions> options) : IEmailSender
{
    private readonly NotificationOptions _options = options.Value;

    public async Task SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.SmtpHost) || string.IsNullOrWhiteSpace(_options.FromEmail))
        {
            throw new InvalidOperationException("Notifications are enabled but SMTP configuration is incomplete.");
        }

        using var message = new MailMessage(_options.FromEmail, toEmail, subject, body)
        {
            IsBodyHtml = false
        };

        using var smtpClient = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            EnableSsl = _options.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            smtpClient.Credentials = new NetworkCredential(_options.Username, _options.Password);
        }

        // SmtpClient has no native cancellation token support.
        await Task.Run(() => smtpClient.Send(message), cancellationToken);
    }
}
