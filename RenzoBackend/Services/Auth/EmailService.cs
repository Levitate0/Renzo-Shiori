using RenzoBackend.Models.Dto;
using RenzoBackend.Services.Settings;
using System.Net;
using System.Net.Mail;

namespace RenzoBackend.Services.Auth;

/// <summary>
/// Sends transactional email (password resets, test messages) through a
/// user-configured external SMTP relay (Settings → Security → Email). Renzo
/// only ever *submits* mail to a provider (Gmail, Brevo, SendGrid, …) on the
/// submission port — it never hosts an SMTP server itself, so ISP blocks on
/// inbound port 25 hosting don't affect it.
/// </summary>
public class EmailService
{
    private readonly SettingsService _settings;
    private readonly ILogger _logger;

    public EmailService(SettingsService settings, ILogger<EmailService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// True when enough SMTP settings are present to attempt a send.
    /// </summary>
    public async Task<bool> IsConfiguredAsync(CancellationToken token = default)
    {
        SettingsDto s = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
        return IsConfigured(s);
    }

    private static bool IsConfigured(SettingsDto s) =>
        !string.IsNullOrWhiteSpace(s.SmtpHost) && !string.IsNullOrWhiteSpace(s.SmtpFromAddress);

    /// <summary>
    /// Sender display name shown in the recipient's inbox — "Renzō on
    /// {public host}" so a person with accounts on several instances can tell
    /// them apart. The host comes from the same public-URL resolution the invite
    /// links use (ExternalDomain, else the best AllowedOrigins entry). Falls back
    /// to plain "Renzō" when nothing but localhost is available.
    /// </summary>
    private static string BuildSenderName(SettingsDto s)
    {
        string baseUrl = InviteUrlResolver.ResolveBaseUrl(s);
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri)
            && !string.IsNullOrEmpty(uri.Host)
            && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && !uri.IsLoopback)
        {
            return $"Renzō on {uri.Authority}";
        }
        return "Renzō";
    }

    /// <summary>
    /// Sends a plain-text email. Returns null on success or a short
    /// human-readable error message on failure (so admins can see exactly why
    /// their relay rejected the message when using the test button).
    /// </summary>
    public async Task<string?> SendAsync(string toAddress, string subject, string body, CancellationToken token = default)
    {
        SettingsDto s = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
        if (!IsConfigured(s))
            return "SMTP is not configured (host and from-address are required).";

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(s.SmtpFromAddress, BuildSenderName(s)),
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };
            message.To.Add(new MailAddress(toAddress));

            using var client = new SmtpClient(s.SmtpHost, s.SmtpPort > 0 ? s.SmtpPort : 587)
            {
                EnableSsl = s.SmtpUseSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 15000
            };
            if (!string.IsNullOrWhiteSpace(s.SmtpUsername))
                client.Credentials = new NetworkCredential(s.SmtpUsername, s.SmtpPassword ?? "");

            await client.SendMailAsync(message, token).ConfigureAwait(false);
            _logger.LogInformation("Sent email '{Subject}' via {Host}:{Port}", subject, s.SmtpHost, s.SmtpPort);
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send email via {Host}:{Port}", s.SmtpHost, s.SmtpPort);
            return ex.Message;
        }
    }
}
