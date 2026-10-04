using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Acropolis.Identity.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Acropolis.Api.Tests;

public sealed class IdentityConfigurationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("https://localhost/path")]
    [InlineData("https://user:password@localhost")]
    [InlineData("https://localhost?token=private-value")]
    [InlineData("https://localhost#token")]
    public async Task ProductionRejectsOriginsWithoutLeakingValues(string origin)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Validator(new IdentitySettings { PublicOrigin = origin }).StartAsync(Token));
        Assert.Equal("Identity public origin is invalid.", error.Message);
        Assert.DoesNotContain("private-value", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionRequiresPasswordProtectedDurableKeysAuthenticatedSmtpAndExactProxy()
    {
        using var temporary = new TemporaryKeys();
        var settings = temporary.Settings();
        await Validator(settings).StartAsync(Token);
        settings.DataProtection.CertificatePassword = "";
        Assert.Equal("Identity key protection configuration is required.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Validator(settings).StartAsync(Token))).Message);
        settings.DataProtection.CertificatePassword = TemporaryKeys.Password;
        settings.Smtp.Username = "";
        Assert.Equal("Identity mail configuration is invalid.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Validator(settings).StartAsync(Token))).Message);
        settings.Smtp.Username = "test";
        settings.Smtp.Password = "";
        Assert.Equal("Identity mail configuration is invalid.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Validator(settings).StartAsync(Token))).Message);
        settings.Smtp.Password = TemporaryKeys.Password;
        settings.Smtp.Security = "none";
        Assert.Equal("Identity mail configuration is invalid.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Validator(settings).StartAsync(Token))).Message);
        settings.Smtp.Security = "starttls";
        settings.KnownProxies = "0.0.0.0/0";
        Assert.Equal("Identity proxy configuration is invalid.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Validator(settings).StartAsync(Token))).Message);
        await Validator(new(), "Development").StartAsync(Token);
        await Validator(new(), "Testing").StartAsync(Token);
    }

    [Fact]
    public async Task ExplicitEmailDeferralDoesNotWeakenKeysOriginOrProxyValidation()
    {
        using var temporary = new TemporaryKeys();
        var settings = temporary.Settings();
        settings.EmailEnabled = false;
        settings.Smtp = new();
        await Validator(settings).StartAsync(Token);
        settings.EmailEnabled = true;
        Assert.Equal("Identity mail configuration is invalid.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Validator(settings).StartAsync(Token))).Message);
        settings.EmailEnabled = false;
        settings.KnownProxies = "";
        Assert.Equal("Identity proxy configuration is invalid.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Validator(settings).StartAsync(Token))).Message);
        settings.KnownProxies = "127.0.0.1";
        settings.DataProtection.CertificatePassword = "";
        Assert.Equal("Identity key protection configuration is required.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Validator(settings).StartAsync(Token))).Message);
    }

    [Fact]
    public void DeferredEmailIgnoresUnusedSmtpConfigurationButEnabledEmailStillValidatesBinding()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Identity:EmailEnabled"] = "false",
            ["Identity:PublicOrigin"] = "https://localhost",
            ["Identity:KnownProxies"] = "127.0.0.1",
            ["Identity:Smtp:Port"] = "unused-not-a-number"
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddChannelIdentity(configuration);
        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IOptions<IdentitySettings>>().Value;
        Assert.False(settings.EmailEnabled);
        Assert.Equal("https://localhost", settings.PublicOrigin);
        Assert.Equal("127.0.0.1", settings.KnownProxies);
        Assert.Equal(587, settings.Smtp.Port);
        configuration["Identity:EmailEnabled"] = "true";
        Assert.Throws<InvalidOperationException>(() => IdentitySettings.BindFrom(configuration));
        configuration["Identity:Smtp:Port"] = "465";
        Assert.Equal(465, IdentitySettings.BindFrom(configuration).Smtp.Port);
        configuration["Identity:EmailEnabled"] = "not-a-boolean";
        Assert.Throws<InvalidOperationException>(() => IdentitySettings.BindFrom(configuration));
    }

    [Fact]
    public void KeyRingSurvivesAProviderRestartAndStoredKeysAreEncrypted()
    {
        using var temporary = new TemporaryKeys();
        var services = new ServiceCollection().AddLogging();
        services.AddChannelIdentity(temporary.Configuration());
        using var first = services.BuildServiceProvider();
        var protectedValue = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("restart-test-value");
        var xml = string.Join("", Directory.GetFiles(temporary.Directory, "key-*.xml").Select(File.ReadAllText));
        Assert.Contains("encryptedSecret", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("restart-test-value", xml, StringComparison.Ordinal);
        services = new ServiceCollection().AddLogging();
        services.AddChannelIdentity(temporary.Configuration());
        using var restarted = services.BuildServiceProvider();
        Assert.Equal("restart-test-value", restarted.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedValue));
    }

    [Fact]
    public void PublicOnlyOrInvalidCertificateCannotStartKeyProtection()
    {
        using var temporary = new TemporaryKeys();
        File.WriteAllBytes(temporary.Certificate, temporary.PublicCertificate);
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddLogging().AddChannelIdentity(temporary.Configuration()));
        Assert.Equal("Identity key protection requires a private certificate.", error.Message);
        File.WriteAllText(temporary.Certificate, "invalid-private-material");
        error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddLogging().AddChannelIdentity(temporary.Configuration()));
        Assert.Equal("Identity key protection certificate is invalid.", error.Message);
        Assert.DoesNotContain("invalid-private-material", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(TemporaryKeys.Password, error.ToString(), StringComparison.Ordinal);
    }

    private static IdentityConfigurationValidator Validator(IdentitySettings settings, string environment = "Production") => new(Options.Create(settings), new EnvironmentStub { EnvironmentName = environment });

    private sealed class EnvironmentStub : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Acropolis.Api";
        public string ContentRootPath { get; set; } = "/tmp";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TemporaryKeys : IDisposable
    {
        public const string Password = "Synthetic QA private phrase";
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "acropolis-key-test-" + Guid.NewGuid().ToString("N"));
        public string Certificate => Path.Combine(Directory, "protector.pfx");
        public byte[] PublicCertificate { get; }
        public TemporaryKeys()
        {
            System.IO.Directory.CreateDirectory(Directory);
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=Acropolis QA only", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(Certificate, certificate.Export(X509ContentType.Pfx, Password));
            using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
            PublicCertificate = publicOnly.Export(X509ContentType.Pfx, Password);
        }
        public IdentitySettings Settings() => new()
        {
            PublicOrigin = "https://localhost",
            KnownProxies = "127.0.0.1",
            DataProtection = new() { KeyRingPath = Directory, CertificatePath = Certificate, CertificatePassword = Password },
            Smtp = new() { Host = "mail.example.test", Port = 587, Username = "test", Password = Password, FromEmail = "test@example.test", Security = "starttls" }
        };
        public IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Identity:DataProtection:KeyRingPath"] = Directory,
            ["Identity:DataProtection:CertificatePath"] = Certificate,
            ["Identity:DataProtection:CertificatePassword"] = Password
        }).Build();
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
