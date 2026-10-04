using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Acropolis.Identity.Infrastructure;

public interface IIdentityMailer { Task SendAsync(MailPayload payload, CancellationToken token); }
public sealed class SmtpIdentityMailer(IOptions<IdentitySettings> configuration) : IIdentityMailer
{
    public async Task SendAsync(MailPayload payload, CancellationToken token)
    {
        var settings = configuration.Value;
        var route = payload.Purpose == "confirm" ? "/confirm-email" : "/reset-password";
        var link = settings.PublicOrigin.TrimEnd('/') + route + "#userId=" + payload.UserId + "&token=" + Uri.EscapeDataString(payload.Token);
        var message = new MimeMessage();
        message.MessageId = payload.MessageId.ToString("N") + "@acropolis-channel";
        message.From.Add(new MailboxAddress(settings.Smtp.FromName, settings.Smtp.FromEmail));
        message.To.Add(MailboxAddress.Parse(payload.Email));
        message.Subject = payload.Purpose == "confirm" ? "Confirma tu correo en Acropolis Channel" : "Restablece tu contraseña de Acropolis Channel";
        message.Body = new TextPart("plain") { Text = "Para continuar, abre este enlace:\n" + link + "\nSi no solicitaste esta acción, ignora este correo." };
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
        if (!configuration.Value.EmailEnabled) throw new InvalidOperationException("Identity email is disabled.");
        var settings = configuration.Value.Smtp;
        if (settings.Security is not ("ssl" or "starttls") && !(settings.Security == "none" && Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Testing"))
            throw new InvalidOperationException("SMTP requires an explicit secure transport.");
        client.Timeout = 15000;
        var security = settings.Security switch { "ssl" => SecureSocketOptions.SslOnConnect, "none" => SecureSocketOptions.None, _ => SecureSocketOptions.StartTls };
        await client.ConnectAsync(settings.Host, settings.Port, security, token);
        if (!string.IsNullOrEmpty(settings.Username)) await client.AuthenticateAsync(settings.Username, settings.Password, token);
    }
}
public sealed class OutboxDispatcher(IdentityDbContext database, IDataProtectionProvider protection, IIdentityMailer mailer, TimeProvider clock, IOptions<IdentitySettings> settings)
{
    private readonly IDataProtector protector = protection.CreateProtector("Acropolis.Identity.Outbox.v1");
    public async Task<bool> DispatchAsync(CancellationToken token)
    {
        if (!settings.Value.EmailEnabled) return false;
        var now = clock.GetUtcNow();
        OutboxMessage? message;
        var owner = Guid.NewGuid().ToString("N");
        await using (var transaction = await database.Database.BeginTransactionAsync(token))
        {
            message = await database.Outbox.FromSqlInterpolated(
                $"SELECT * FROM identity.\"Outbox\" WHERE (\"Status\"='pending' AND \"NextAttemptUtc\"<={now}) OR (\"Status\"='sending' AND \"LeaseExpiresUtc\"<={now}) ORDER BY \"NextAttemptUtc\" LIMIT 1 FOR UPDATE SKIP LOCKED").SingleOrDefaultAsync(token);
            if (message is null) return false;
            var flow = await database.Flows.AsNoTracking().SingleOrDefaultAsync(x => x.Id == message.FlowId, token);
            if (flow is null || flow.ConsumedUtc is not null || flow.ExpiresUtc <= now || message.Attempts >= 5)
            {
                message.Status = message.Attempts >= 5 ? "failed" : "cancelled";
                message.Payload = "";
            }
            else
            {
                message.Status = "sending";
                message.LeaseOwner = owner;
                message.LeaseExpiresUtc = now.AddSeconds(30);
                message.Attempts++;
            }
            await database.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        if (message.Status != "sending") return true;
        var delivered = false;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var payload = JsonSerializer.Deserialize<MailPayload>(protector.Unprotect(message.Payload))!;
                await mailer.SendAsync(payload, deadline.Token);
                delivered = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { /* No token, recipient, or provider details are logged. */ }
        }
        var status = delivered ? "sent" : message.Attempts >= 5 ? "failed" : "pending";
        var next = clock.GetUtcNow().AddSeconds(Math.Min(600, 5 * Math.Pow(2, message.Attempts)));
        await database.Outbox.Where(x => x.Id == message.Id && x.Status == "sending" && x.LeaseOwner == owner)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, status)
                .SetProperty(x => x.Payload, status == "pending" ? message.Payload : "")
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseExpiresUtc, (DateTimeOffset?)null)
                .SetProperty(x => x.NextAttemptUtc, next), token);
        database.ChangeTracker.Clear();
        return true;
    }
}
public sealed class OutboxWorker(IServiceScopeFactory scopes, ILogger<OutboxWorker> logger, IOptions<IdentitySettings> settings) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.EmailEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                for (var index = 0; index < 20; index++)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    if (!await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync(stoppingToken)) break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Identity delivery is temporarily unavailable."); }
        }
    }
}
