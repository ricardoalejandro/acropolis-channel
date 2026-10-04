using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Acropolis.Identity.Application;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acropolis.Identity.Infrastructure;

public static class IdentityRegistration
{
    public static IServiceCollection AddChannelIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IdentitySettings>(configuration.GetSection("Identity"));
        services.AddSingleton(TimeProvider.System);
        services.AddDbContextFactory<IdentityDbContext>((provider, options) =>
            ConfigureDatabase(options, provider.GetRequiredService<IConfiguration>().GetConnectionString("Database")!));
        services.AddIdentityCore<ChannelUser>(options =>
        {
            options.Password.RequiredLength = 15;
            options.Password.RequiredUniqueChars = 1;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.User.RequireUniqueEmail = true;
            options.User.AllowedUserNameCharacters = string.Empty;
            options.SignIn.RequireConfirmedEmail = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        }).AddEntityFrameworkStores<IdentityDbContext>();
        var protection = services.AddDataProtection().SetApplicationName("AcropolisChannel");
        var settings = configuration.GetSection("Identity").Get<IdentitySettings>() ?? new();
        if (!string.IsNullOrWhiteSpace(settings.DataProtection.KeyRingPath))
        {
            protection.PersistKeysToFileSystem(new DirectoryInfo(settings.DataProtection.KeyRingPath));
            if (!string.IsNullOrWhiteSpace(settings.DataProtection.CertificatePath))
            {
                X509Certificate2 certificate;
                try
                {
                    certificate = X509CertificateLoader.LoadPkcs12FromFile(settings.DataProtection.CertificatePath,
                        settings.DataProtection.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
                }
                catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException("Identity key protection certificate is invalid.");
                }
                if (!certificate.HasPrivateKey) throw new InvalidOperationException("Identity key protection requires a private certificate.");
                protection.ProtectKeysWithCertificate(certificate).UnprotectKeysWithAnyCertificate(certificate);
            }
        }
        services.AddSingleton<ITicketStore, PostgresTicketStore>();
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme).Configure<ITicketStore, TimeProvider>((options, store, clock) =>
        {
            options.SessionStore = store;
            options.TimeProvider = clock;
        });
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IIdentityMailer, SmtpIdentityMailer>();
        services.AddScoped<OutboxDispatcher>();
        services.AddHostedService<OutboxWorker>();
        return services;
    }
    public static void ConfigureDatabase(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, provider => provider.MigrationsHistoryTable(IdentityDbContext.HistoryTable, IdentityDbContext.Schema).CommandTimeout(3));
}
