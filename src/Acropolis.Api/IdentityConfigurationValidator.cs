using System.Net;
using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;
using Microsoft.Extensions.Options;

public sealed class IdentityConfigurationValidator(IOptions<IdentitySettings> options, IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (environment.IsEnvironment("Testing") || environment.IsDevelopment()) return Task.CompletedTask;
        var settings = options.Value;
        var smtp = settings.Smtp;
        var keys = settings.DataProtection;
        if (!Uri.TryCreate(settings.PublicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" || origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            throw new InvalidOperationException("Identity public origin is invalid.");
        if (string.IsNullOrWhiteSpace(keys.KeyRingPath) || !Path.IsPathFullyQualified(keys.KeyRingPath) || string.IsNullOrWhiteSpace(keys.CertificatePath) || string.IsNullOrWhiteSpace(keys.CertificatePassword) || !File.Exists(keys.CertificatePath))
            throw new InvalidOperationException("Identity key protection configuration is required.");
        if (settings.EmailEnabled && (string.IsNullOrWhiteSpace(smtp.Host) || smtp.Port is < 1 or > 65535 || !IdentityRules.ValidEmail(smtp.FromEmail) || smtp.Security is not ("starttls" or "ssl") || string.IsNullOrWhiteSpace(smtp.Username) || string.IsNullOrWhiteSpace(smtp.Password)))
            throw new InvalidOperationException("Identity mail configuration is invalid.");
        var proxies = settings.KnownProxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (proxies.Length == 0 || proxies.Any(proxy => !IPAddress.TryParse(proxy, out _))) throw new InvalidOperationException("Identity proxy configuration is invalid.");
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
