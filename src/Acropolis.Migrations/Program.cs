using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Acropolis.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Acropolis.Migrations;

internal static class EntryPoint
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["smtp-check"])
        {
            try
            {
                var settings = new ConfigurationBuilder().AddEnvironmentVariables().Build().GetSection("Identity").Get<IdentitySettings>() ?? new();
                using var smtpDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await new SmtpIdentityMailer(Options.Create(settings)).CheckAsync(smtpDeadline.Token);
                Console.WriteLine("{\"status\":\"smtp_ready\"}");
                return 0;
            }
            catch (Exception)
            {
                Console.Error.WriteLine("{\"error\":\"smtp_unavailable\"}");
                return 1;
            }
        }
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Database");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine("{\"error\":\"database_configuration_missing\"}");
            return 1;
        }
        using var deadline = new CancellationTokenSource(args.FirstOrDefault() == "qa-seed" ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(30));
        try
        {
            if (args.Length == 0)
            {
                await new ChannelMigrationRunner().RunAsync(connection, deadline.Token);
                Console.WriteLine("{\"status\":\"migrations_current\"}");
                return 0;
            }
            var options = new DbContextOptionsBuilder<IdentityDbContext>();
            IdentityRegistration.ConfigureDatabase(options, connection);
            await using var database = new IdentityDbContext(options.Options);
            var operations = new IdentityOperations(database, TimeProvider.System);
            switch (args)
            {
                case ["prune-identity"]:
                    await operations.PruneAsync(deadline.Token); break;
                case ["bootstrap-admin", "--email", var email]:
                    await operations.BootstrapAsync(email, deadline.Token); break;
                case ["recovery-invalidate", "--maintenance"]:
                    await operations.InvalidateRecoveryAsync(deadline.Token); break;
                case ["recovery-revalidate", "--email", var email, "--maintenance"]:
                    await operations.RevalidateAsync(email, false, deadline.Token); break;
                case ["recover-admin", "--email", var email, "--maintenance"]:
                    await operations.RevalidateAsync(email, true, deadline.Token); break;
                case ["qa-seed", "--count", var raw] when int.TryParse(raw, out var count):
                    await operations.SeedQaAsync(count, Environment.GetEnvironmentVariable("QA_SEED_PASSWORD") ?? "", deadline.Token); break;
                default: throw new InvalidOperationException("Invalid administrative command.");
            }
            Console.WriteLine("{\"status\":\"identity_operation_complete\"}");
            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("{\"error\":\"identity_operation_failed\"}");
            return 1;
        }
    }
}
