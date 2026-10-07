using System.Globalization;
using Acropolis.Subscriptions.Application;
using Acropolis.Subscriptions.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Npgsql;

namespace Acropolis.Identity.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class SmtpEmailTransportTests(IdentityFixture database)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("ssl")]
    [InlineData("starttls")]
    public async Task SecureWirePreservesTransactionalIdentifiersAndLegacyIdentityMessages(string security)
    {
        await using var smtp = new LoopbackSmtp(database, security);
        var settings = smtp.Settings();
        var transport = new SmtpEmailTransport(Options.Create(settings));
        var identity = new SmtpIdentityMailer(Options.Create(settings));
        Assert.True(transport.IsAvailable);
        Assert.Equal(settings.PublicOrigin, transport.PublicOrigin);
        await transport.CheckAsync(Token);
        await identity.CheckAsync(Token);
        var id = Guid.NewGuid();
        var email = new TransactionalEmail(id, LoopbackSmtp.Recipient, "Tu suscripción de Acrópolis", "Aviso sintético de QA. Tu periodo sigue vigente.");
        await transport.SendAsync(email, Token);
        await transport.SendAsync(email, Token);
        var user = Guid.NewGuid();
        var confirmation = new MailPayload(LoopbackSmtp.Recipient, user, "synthetic +/# token", "confirm", Guid.NewGuid());
        var reset = new MailPayload(LoopbackSmtp.Recipient, user, "synthetic reset token", "reset", Guid.NewGuid());
        await identity.SendAsync(confirmation, Token);
        await identity.SendAsync(reset, Token);
        var messages = smtp.Messages;
        Assert.Equal(4, messages.Length);
        Assert.All(messages, message =>
        {
            var from = Assert.Single(message.From.Mailboxes);
            Assert.Equal(LoopbackSmtp.Sender, from.Address);
            Assert.Equal("Acrópolis Channel QA", from.Name);
            Assert.Equal(LoopbackSmtp.Recipient, Assert.Single(message.To.Mailboxes).Address);
            Assert.Null(message.HtmlBody);
        });
        Assert.Equal(id.ToString("N") + "@example.test", messages[0].MessageId);
        Assert.Equal(messages[0].MessageId, messages[1].MessageId);
        Assert.Equal(email.Subject, messages[0].Subject);
        Assert.Equal(email.Text, Text(messages[0]).TrimEnd('\r', '\n'));
        Assert.Equal(email.Text, Text(messages[1]).TrimEnd('\r', '\n'));
        AssertLegacy(messages[2], confirmation, settings.PublicOrigin, "/confirm-email", "Confirma tu correo en Acropolis Channel");
        AssertLegacy(messages[3], reset, settings.PublicOrigin, "/reset-password", "Restablece tu contraseña de Acropolis Channel");
        Assert.Equal(6, smtp.AcceptedConnections);
        Assert.Equal(6, smtp.SecureConnections);
        Assert.Equal(6, smtp.AuthenticatedConnections);
        Assert.Equal(0, smtp.AuthenticationBeforeTls);
    }

    [Theory]
    [InlineData("ssl")]
    [InlineData("starttls")]
    public async Task UntrustedCertificateIsRejectedBeforeAuthenticationOrMessageDelivery(string security)
    {
        await using var smtp = new LoopbackSmtp(database, security, trustCertificate: false);
        var transport = new SmtpEmailTransport(Options.Create(smtp.Settings()));
        await Assert.ThrowsAsync<SslHandshakeException>(() => transport.SendAsync(Email(), Token));
        Assert.Equal(1, smtp.AcceptedConnections);
        Assert.Equal(0, smtp.AuthenticatedConnections);
        Assert.Equal(0, smtp.AuthenticationBeforeTls);
        Assert.Empty(smtp.Messages);
    }

    [Fact]
    public async Task RequiredStartTlsNeverFallsBackToPlainAuthenticationOrDelivery()
    {
        await using var smtp = new LoopbackSmtp(database, "starttls", offerStartTls: false);
        var transport = new SmtpEmailTransport(Options.Create(smtp.Settings()));
        await Assert.ThrowsAsync<NotSupportedException>(() => transport.SendAsync(Email(), Token));
        Assert.Equal(1, smtp.AcceptedConnections);
        Assert.Equal(0, smtp.SecureConnections);
        Assert.Equal(0, smtp.AuthenticatedConnections);
        Assert.Equal(0, smtp.AuthenticationBeforeTls);
        Assert.Empty(smtp.Messages);
    }

    [Fact]
    public async Task EmailDisabledAndProductionPlainSmtpAreRejectedWithoutOpeningASocket()
    {
        await using var smtp = new LoopbackSmtp(database, "ssl");
        var settings = smtp.Settings();
        settings.EmailEnabled = false;
        var transport = new SmtpEmailTransport(Options.Create(settings));
        var identity = new SmtpIdentityMailer(Options.Create(settings));
        Assert.False(transport.IsAvailable);
        Assert.Equal("Identity email is disabled.", (await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync(Email(), Token))).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.CheckAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => identity.SendAsync(new(LoopbackSmtp.Recipient, Guid.NewGuid(), "synthetic token", "confirm", Guid.NewGuid()), Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => identity.CheckAsync(Token));
        settings.EmailEnabled = true;
        settings.Smtp.Security = "none";
        var previous = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");
            Assert.True(transport.IsAvailable);
            Assert.Equal("SMTP requires an explicit secure transport.", (await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync(Email(), Token))).Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => identity.CheckAsync(Token));
        }
        finally { Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previous); }
        Assert.Equal(0, smtp.AcceptedConnections);
        Assert.Empty(smtp.Messages);
    }

    [Fact]
    public async Task SubscriptionIntentsReachTheirHolderThroughPostgresAndValidatedTlsForEachCommittedEvent()
    {
        await database.ResetAsync(Token);
        await using var smtp = new LoopbackSmtp(database, "ssl");
        await using var origin = new IdentityApiFactory(database);
        origin.Clock.Advance(TimeSpan.FromTicks(-(origin.Clock.GetUtcNow().Ticks % 10)));
        var settings = smtp.Settings();
        await using var api = origin.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Identity:PublicOrigin"] = settings.PublicOrigin }));
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<IdentitySettings>(options =>
                {
                    options.EmailEnabled = true; options.PublicOrigin = settings.PublicOrigin; options.Smtp = settings.Smtp;
                });
                services.PostConfigure<SubscriptionNotificationOptions>(options => { options.Enabled = true; options.RunInTesting = false; });
            });
        });
        ChannelUser manager;
        ChannelUser holder;
        await using (var setup = api.Services.CreateAsyncScope())
        {
            Assert.IsType<SmtpEmailTransport>(setup.ServiceProvider.GetRequiredService<ITransactionalEmailSender>());
            Assert.IsType<AccountNotificationRecipient>(setup.ServiceProvider.GetRequiredService<IAccountNotificationRecipient>());
            Assert.Equal(settings.PublicOrigin, setup.ServiceProvider.GetRequiredService<IConfiguration>()["Identity:PublicOrigin"]);
            var smtpOptions = setup.ServiceProvider.GetRequiredService<IOptions<IdentitySettings>>().Value;
            Assert.Equal(settings.PublicOrigin, smtpOptions.PublicOrigin); Assert.Equal("127.0.0.1", smtpOptions.Smtp.Host);
            Assert.Equal(settings.Smtp.Port, smtpOptions.Smtp.Port); Assert.Equal("ssl", smtpOptions.Smtp.Security);
            var options = setup.ServiceProvider.GetRequiredService<IOptions<SubscriptionNotificationOptions>>().Value;
            Assert.True(options.Enabled); Assert.False(options.RunInTesting);
            var users = setup.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>();
            manager = new() { Id = Guid.NewGuid(), UserName = "smtp-manager@example.test", Email = "smtp-manager@example.test", DisplayName = "QA manager", EmailConfirmed = true, SubscriptionsManage = true };
            holder = new() { Id = Guid.NewGuid(), UserName = LoopbackSmtp.Recipient, Email = LoopbackSmtp.Recipient, DisplayName = "QA holder", EmailConfirmed = true };
            const string password = "Synthetic subscription SMTP phrase 2026";
            Assert.True((await users.CreateAsync(manager, password)).Succeeded);
            Assert.True((await users.CreateAsync(holder, password)).Succeeded);
            Assert.False(holder.IsDisabled); Assert.False(holder.RevalidationRequired); Assert.True(holder.EmailConfirmed);
            Assert.False(manager.IsDisabled); Assert.True(manager.EmailConfirmed); Assert.True(manager.SubscriptionsManage);
        }
        async Task<SubscriptionView> AssignAsync(DateTimeOffset start, string? version = null)
        {
            await using var scope = api.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<ISubscriptionService>().AssignAsync(manager.Id, holder.Id,
                new("annual", start, version, "Synthetic SMTP integration assignment"), Token);
            Assert.True(result.Succeeded);
            return Assert.IsType<SubscriptionView>(result.Value);
        }
        async Task<bool> DispatchAsync()
        {
            await using var scope = api.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<SubscriptionNotificationDispatcher>().DispatchAsync(Token);
        }
        async Task<int> ScheduleAsync()
        {
            await using var scope = api.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<SubscriptionNotificationScheduler>().ScheduleAsync(Token);
        }
        async Task<SubscriptionNotification[]> RowsAsync()
        {
            await using var scope = api.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<SubscriptionsDbContext>().Notifications.AsNoTracking().ToArrayAsync(Token);
        }
        var assigned = await AssignAsync(origin.Clock.GetUtcNow());
        var oldEnd = Assert.IsType<DateTimeOffset>(assigned.ExpiresUtc);
        var intent = Assert.Single(await RowsAsync());
        Assert.Equal("assigned", intent.Kind); Assert.Equal("pending", intent.Status); Assert.Equal(0, intent.Attempts);
        Assert.Empty(smtp.Messages);
        Assert.True(await DispatchAsync());
        Assert.Single(smtp.Messages);
        var acknowledged = Assert.Single(await RowsAsync());
        Assert.Equal("sent", acknowledged.Status); Assert.Equal(1, acknowledged.Attempts); Assert.Null(acknowledged.LeaseOwner);
        var renewed = await AssignAsync(oldEnd, assigned.Version);
        var renewedEnd = Assert.IsType<DateTimeOffset>(renewed.ExpiresUtc);
        Assert.Equal(assigned.StartsUtc, renewed.StartsUtc); Assert.Equal(oldEnd.AddYears(1), renewedEnd);
        Assert.False(await DispatchAsync()); // Shared durable budget has not reached its next fifteen-second slot.
        Assert.Single(smtp.Messages);
        origin.Clock.Advance(TimeSpan.FromSeconds(15));
        Assert.True(await DispatchAsync()); Assert.Equal(2, smtp.Messages.Length);
        origin.Clock.Advance(renewedEnd - origin.Clock.GetUtcNow() - TimeSpan.FromDays(7));
        Assert.Equal(renewedEnd.AddDays(-7), origin.Clock.GetUtcNow());
        Assert.Equal(1, await ScheduleAsync()); Assert.Equal(0, await ScheduleAsync());
        Assert.True(await DispatchAsync());
        var rows = await RowsAsync();
        var messages = smtp.Messages;
        Assert.Equal(3, rows.Length); Assert.Equal(3, messages.Length);
        Assert.Equal(3, rows.Select(row => row.Id).Distinct().Count());
        Assert.Equal(2, rows.Count(row => row.SourceAuditId is not null));
        Assert.Null(Assert.Single(rows, row => row.Kind == "expiring").SourceAuditId);
        Assert.Equal(3, messages.Select(message => message.MessageId).Distinct().Count());
        Assert.Equal(new[] { "assigned", "expiring", "renewed" }, rows.Select(row => row.Kind).OrderBy(kind => kind, StringComparer.Ordinal).ToArray());
        Assert.Equal(assigned.ExpiresUtc, Assert.Single(rows, row => row.Kind == "assigned").ExpiresUtc);
        Assert.Equal(renewed.ExpiresUtc, Assert.Single(rows, row => row.Kind == "renewed").ExpiresUtc);
        Assert.Equal(renewed.ExpiresUtc, Assert.Single(rows, row => row.Kind == "expiring").ExpiresUtc);
        foreach (var row in rows)
        {
            Assert.Equal(holder.Id, row.UserId); Assert.Equal(assigned.Id, row.SubscriptionId);
            Assert.Equal("annual", row.Plan); Assert.Equal(assigned.StartsUtc, row.StartsUtc);
            Assert.Equal("sent", row.Status); Assert.Equal(1, row.Attempts); Assert.Null(row.LeaseOwner); Assert.Null(row.LeaseExpiresUtc);
            var message = Assert.Single(messages, item => item.MessageId == row.Id.ToString("N") + "@example.test");
            Assert.Equal(holder.Email, Assert.Single(message.To.Mailboxes).Address);
            Assert.Empty(message.Cc); Assert.Empty(message.Bcc); Assert.Null(message.HtmlBody);
            Assert.Equal(LoopbackSmtp.Sender, Assert.Single(message.From.Mailboxes).Address);
            var expectedSubject = row.Kind switch
            {
                "assigned" => "Acrópolis Channel — Tu plan fue asignado",
                "renewed" => "Acrópolis Channel — Tu plan fue renovado",
                _ => "Acrópolis Channel — Tu plan está próximo a vencer"
            };
            Assert.Equal(expectedSubject, message.Subject);
            var body = Text(message).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Contains("Plan: Anual", body, StringComparison.Ordinal);
            Assert.Contains("Inicio: " + row.StartsUtc.ToOffset(TimeSpan.FromHours(-5)).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
            Assert.Contains("Vencimiento: " + row.ExpiresUtc!.Value.ToOffset(TimeSpan.FromHours(-5)).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
            Assert.Contains("La renovación es manual.", body, StringComparison.Ordinal);
            Assert.Contains(settings.PublicOrigin.TrimEnd('/') + "/profile/subscription", body, StringComparison.Ordinal);
            Assert.Contains("aviso transaccional", body, StringComparison.Ordinal); Assert.Contains("no es publicidad", body, StringComparison.Ordinal);
            Assert.DoesNotContain(manager.Email!, body, StringComparison.Ordinal);
        }
        origin.Clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(0, await ScheduleAsync()); Assert.False(await DispatchAsync());
        Assert.Equal(3, (await RowsAsync()).Length); Assert.Equal(3, smtp.Messages.Length);
        Assert.Equal(3, smtp.AcceptedConnections); Assert.Equal(3, smtp.SecureConnections); Assert.Equal(3, smtp.AuthenticatedConnections);
        Assert.Equal(0, smtp.AuthenticationBeforeTls);
    }

    private static string Text(MimeMessage message) => Assert.IsType<TextPart>(message.Body).Text;
    private static TransactionalEmail Email() => new(Guid.NewGuid(), LoopbackSmtp.Recipient, "QA only", "Synthetic transport test.");
    private static void AssertLegacy(MimeMessage message, MailPayload payload, string origin, string route, string subject)
    {
        Assert.Equal(payload.MessageId.ToString("N") + "@acropolis-channel", message.MessageId);
        Assert.Equal(subject, message.Subject);
        Assert.Equal("Para continuar, abre este enlace:\n" + origin.TrimEnd('/') + route + "#userId=" + payload.UserId + "&token=" + Uri.EscapeDataString(payload.Token) + "\nSi no solicitaste esta acción, ignora este correo.", Text(message).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n'));
    }

    // A bounded QA receiver, never a hosted mail service. The normal client certificate validator is unchanged.
    private sealed class LoopbackSmtp : IAsyncDisposable
    {
        internal const string Sender = "notificaciones@example.test";
        internal const string Recipient = "smtp-recipient@example.test";
        private const string Username = "synthetic-smtp-user";
        private const string Password = "Synthetic QA SMTP phrase";
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        private readonly ConcurrentQueue<MimeMessage> messages = new();
        private readonly X509Certificate2 root;
        private readonly X509Certificate2 certificate;
        private readonly X509Store? store;
        private readonly string security;
        private readonly bool offerStartTls;
        private readonly bool expectCertificateRejection;
        private readonly Task loop;
        private TcpClient? active;
        private int acceptedConnections, secureConnections, authenticatedConnections, authenticationBeforeTls;
        public MimeMessage[] Messages => messages.ToArray();
        public int AcceptedConnections => Volatile.Read(ref acceptedConnections);
        public int SecureConnections => Volatile.Read(ref secureConnections);
        public int AuthenticatedConnections => Volatile.Read(ref authenticatedConnections);
        public int AuthenticationBeforeTls => Volatile.Read(ref authenticationBeforeTls);

        public LoopbackSmtp(IdentityFixture database, string security, bool trustCertificate = true, bool offerStartTls = true)
        {
            if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") != "Testing" ||
                !System.Text.RegularExpressions.Regex.IsMatch(new NpgsqlConnectionStringBuilder(database.AdminConnection).Database ?? "", "^acropolis_test_[a-z0-9_]+$"))
                throw new InvalidOperationException("Synthetic SMTP trust requires the isolated Testing database fixture.");
            this.security = security;
            this.offerStartTls = offerStartTls;
            expectCertificateRejection = !trustCertificate;
            (root, certificate) = Certificates();
            try
            {
                if (trustCertificate)
                {
                    store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                    store.Open(OpenFlags.ReadWrite);
                    store.Add(root);
                }
                listener.Start();
                loop = RunAsync();
            }
            catch
            {
                try { if (store is not null) store.Remove(root); }
                finally
                {
                    store?.Dispose(); listener.Stop();
                    certificate.Dispose(); root.Dispose(); stop.Dispose();
                }
                throw;
            }
        }

        public IdentitySettings Settings() => new()
        {
            EmailEnabled = true,
            PublicOrigin = "https://app.example.test/",
            Smtp = new()
            {
                Host = "127.0.0.1",
                Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                Username = Username,
                Password = Password,
                FromEmail = Sender,
                FromName = "Acrópolis Channel QA",
                Security = security
            }
        };

        private async Task RunAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    active = client;
                    if (Interlocked.Increment(ref acceptedConnections) > 16)
                        throw new InvalidOperationException("Synthetic SMTP connection budget exceeded.");
                    try { await ReceiveAsync(client); }
                    catch (Exception error) when (expectCertificateRejection && (error is AuthenticationException or IOException)) { }
                    finally { active = null; }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        }

        private async Task<SslStream> UpgradeAsync(NetworkStream network)
        {
            var ssl = new SslStream(network, leaveInnerStreamOpen: true);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, stop.Token);
                Interlocked.Increment(ref secureConnections);
                return ssl;
            }
            catch { ssl.Dispose(); throw; }
        }

        private static StreamReader Reader(Stream stream) => new(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        private static StreamWriter Writer(Stream stream) => new(stream, Encoding.ASCII, 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        private async Task ReceiveAsync(TcpClient client)
        {
            var network = client.GetStream();
            SslStream? ssl = security == "ssl" ? await UpgradeAsync(network) : null;
            Stream stream = ssl is null ? network : ssl;
            var reader = Reader(stream);
            var writer = Writer(stream);
            var authenticated = false;
            try
            {
                await writer.WriteLineAsync("220 localhost synthetic QA".AsMemory(), stop.Token);
                while (await reader.ReadLineAsync(stop.Token) is { } command)
                {
                    if (command.Length > 65536) throw new InvalidOperationException("Synthetic SMTP line budget exceeded.");
                    if (command.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250-localhost synthetic QA".AsMemory(), stop.Token);
                        if (ssl is null && offerStartTls)
                            await writer.WriteLineAsync("250-STARTTLS".AsMemory(), stop.Token);
                        if (ssl is not null || !offerStartTls)
                            await writer.WriteLineAsync("250-AUTH PLAIN".AsMemory(), stop.Token);
                        await writer.WriteLineAsync("250 SIZE 65536".AsMemory(), stop.Token);
                    }
                    else if (command == "STARTTLS" && ssl is null && offerStartTls)
                    {
                        await writer.WriteLineAsync("220 Ready for TLS".AsMemory(), stop.Token);
                        reader.Dispose(); writer.Dispose();
                        ssl = await UpgradeAsync(network);
                        reader = Reader(ssl); writer = Writer(ssl);
                    }
                    else if (command.StartsWith("AUTH PLAIN", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ssl is null)
                        {
                            Interlocked.Increment(ref authenticationBeforeTls);
                            await writer.WriteLineAsync("538 TLS required".AsMemory(), stop.Token);
                            continue;
                        }
                        var encoded = command.Length > 11 ? command[11..] : "";
                        if (encoded.Length == 0)
                        {
                            await writer.WriteLineAsync("334 ".AsMemory(), stop.Token);
                            encoded = await reader.ReadLineAsync(stop.Token) ?? "";
                        }
                        authenticated = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)) == "\0" + Username + "\0" + Password;
                        if (authenticated) Interlocked.Increment(ref authenticatedConnections);
                        await writer.WriteLineAsync((authenticated ? "235 Authentication successful" : "535 Authentication failed").AsMemory(), stop.Token);
                    }
                    else if (command.StartsWith("MAIL FROM:<" + Sender + ">", StringComparison.OrdinalIgnoreCase) ||
                        command.StartsWith("RCPT TO:<" + Recipient + ">", StringComparison.OrdinalIgnoreCase))
                        await writer.WriteLineAsync((authenticated && ssl is not null ? "250 OK" : "530 TLS and authentication required").AsMemory(), stop.Token);
                    else if (command == "DATA" && authenticated && ssl is not null)
                    {
                        await writer.WriteLineAsync("354 End data with dot".AsMemory(), stop.Token);
                        var data = new StringBuilder();
                        while (true)
                        {
                            var line = await reader.ReadLineAsync(stop.Token) ?? throw new IOException("Synthetic SMTP data was truncated.");
                            if (line == ".") break;
                            if (line.StartsWith("..", StringComparison.Ordinal)) line = line[1..];
                            data.Append(line).Append("\r\n");
                            if (data.Length > 65536) throw new InvalidOperationException("Synthetic SMTP message budget exceeded.");
                        }
                        using var body = new MemoryStream(Encoding.ASCII.GetBytes(data.ToString()));
                        messages.Enqueue(MimeMessage.Load(body, stop.Token));
                        await writer.WriteLineAsync("250 Queued QA message".AsMemory(), stop.Token);
                    }
                    else if (command == "QUIT")
                    {
                        await writer.WriteLineAsync("221 Bye".AsMemory(), stop.Token);
                        return;
                    }
                    else if (command is "RSET" or "NOOP")
                        await writer.WriteLineAsync("250 OK".AsMemory(), stop.Token);
                    else throw new InvalidOperationException("Unexpected synthetic SMTP command.");
                }
            }
            finally { reader.Dispose(); writer.Dispose(); ssl?.Dispose(); }
        }

        private static (X509Certificate2 Root, X509Certificate2 Server) Certificates()
        {
            using var rootKey = RSA.Create(2048);
            var authority = new CertificateRequest("CN=Acropolis SMTP QA " + Guid.NewGuid().ToString("N"), rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            authority.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            authority.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            authority.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(authority.PublicKey, false));
            var now = DateTimeOffset.UtcNow;
            using var issuer = authority.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1));
            using var serverKey = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var issued = request.Create(issuer, now.AddMinutes(-1), now.AddHours(1), RandomNumberGenerator.GetBytes(16));
            return (X509CertificateLoader.LoadCertificate(issuer.Export(X509ContentType.Cert)), issued.CopyWithPrivateKey(serverKey));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                stop.Cancel(); listener.Stop(); active?.Dispose();
                await loop.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                try
                {
                    if (store is not null)
                    {
                        store.Remove(root);
                        Assert.Empty(store.Certificates.Find(X509FindType.FindByThumbprint, root.Thumbprint, false));
                    }
                }
                finally { store?.Dispose(); certificate.Dispose(); root.Dispose(); stop.Dispose(); }
            }
        }
    }
}
