using Acropolis.Identity.Application;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Acropolis.Identity.Infrastructure;

public sealed class SmtpEmailTransport(IOptions<IdentitySettings> configuration) : ITransactionalEmailSender
{
    public bool IsAvailable => configuration.Value.EmailEnabled;
    public string PublicOrigin => configuration.Value.PublicOrigin;
    public Task SendAsync(TransactionalEmail email, CancellationToken token) => SendAsync(email, false, token);
    internal Task SendIdentityAsync(TransactionalEmail email, CancellationToken token) => SendAsync(email, true, token);
    private async Task SendAsync(TransactionalEmail email, bool legacyMessageId, CancellationToken token)
    {
        if (email.MessageId == Guid.Empty || !IdentityRules.ValidEmail(email.Recipient) || string.IsNullOrWhiteSpace(email.Subject) ||
            email.Subject.Length > 200 || email.Subject.Any(char.IsControl) || string.IsNullOrWhiteSpace(email.Text) || email.Text.Length > 32768)
            throw new ArgumentException("Transactional email is invalid.", nameof(email));
        var settings = configuration.Value.Smtp;
        var message = new MimeMessage();
        var from = new MailboxAddress(settings.FromName, settings.FromEmail);
        var domain = from.Address[(from.Address.LastIndexOf('@') + 1)..];
        if (!legacyMessageId && (Uri.CheckHostName(domain) != UriHostNameType.Dns || !domain.Contains('.')))
            throw new InvalidOperationException("Transactional email requires a qualified sender domain.");
        // Keep existing Identity message identifiers compatible; new notices use the configured sender domain.
        message.MessageId = email.MessageId.ToString("N") + "@" + (legacyMessageId ? "acropolis-channel" : domain);
        message.From.Add(from);
        message.To.Add(MailboxAddress.Parse(email.Recipient));
        message.Subject = email.Subject;
        message.Body = new TextPart("plain") { Text = email.Text };
        using var client = new SmtpClient();
        await ConnectAsync(client, token);
        await client.SendAsync(message, token);
        await client.DisconnectAsync(true, token);
    }
    public async Task CheckAsync(CancellationToken token)
    {
        using var client = new SmtpClient();
        await ConnectAsync(client, token);
        await client.DisconnectAsync(true, token);
    }
    private async Task ConnectAsync(SmtpClient client, CancellationToken token)
    {
        if (!IsAvailable) throw new InvalidOperationException("Identity email is disabled.");
        var settings = configuration.Value.Smtp;
        if (settings.Security is not ("ssl" or "starttls") && !(settings.Security == "none" && Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Testing"))
            throw new InvalidOperationException("SMTP requires an explicit secure transport.");
        client.Timeout = 15000;
        var security = settings.Security switch { "ssl" => SecureSocketOptions.SslOnConnect, "none" => SecureSocketOptions.None, _ => SecureSocketOptions.StartTls };
        await client.ConnectAsync(settings.Host, settings.Port, security, token);
        if (!string.IsNullOrEmpty(settings.Username)) await client.AuthenticateAsync(settings.Username, settings.Password, token);
    }
}
