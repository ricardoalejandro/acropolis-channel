using Microsoft.Extensions.Configuration;
using Acropolis.Subscriptions.Infrastructure;
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
                var settings = IdentitySettings.BindFrom(new ConfigurationBuilder().AddEnvironmentVariables().Build());
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
        var operationTimeout = args.FirstOrDefault() is "qa-seed" or "qa-seed-catalog" ? TimeSpan.FromMinutes(15)
            : args is ["recovery-invalidate", "--maintenance"] ? TimeSpan.FromMinutes(5)
            : TimeSpan.FromSeconds(30);
        using var deadline = new CancellationTokenSource(operationTimeout);
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
            database.Database.SetCommandTimeout(30);
            var operations = new IdentityOperations(database, TimeProvider.System);
            switch (args)
            {
                case ["prune-identity"]:
                    await operations.PruneAsync(deadline.Token); break;
                case ["bootstrap-owner", "--email", var email]:
                    await operations.BootstrapOwnerAsync(email, Environment.GetEnvironmentVariable("Identity__OwnerEmail"), deadline.Token); break;
                case ["recover-owner", "--email", var email, "--maintenance"]:
                    await operations.RecoverOwnerAsync(email, Environment.GetEnvironmentVariable("Identity__OwnerEmail"), true, deadline.Token); break;
                case ["bootstrap-admin", "--email", var email]:
                    await operations.BootstrapAsync(email, deadline.Token); break;
                case ["grant-content-manager", "--email", var email]:
                    await operations.SetContentManagerAsync(email, true, deadline.Token); break;
                case ["revoke-content-manager", "--email", var email]:
                    await operations.SetContentManagerAsync(email, false, deadline.Token); break;
                case ["recovery-invalidate", "--maintenance"]:
                    await operations.InvalidateRecoveryAsync(deadline.Token);
                    var subscriptionsOptions = new DbContextOptionsBuilder<SubscriptionsDbContext>();
                    SubscriptionsRegistration.ConfigureDatabase(subscriptionsOptions, connection);
                    await using (var subscriptionsDatabase = new SubscriptionsDbContext(subscriptionsOptions.Options))
                    {
                        subscriptionsDatabase.Database.SetCommandTimeout(30);
                        await new SubscriptionOperations(subscriptionsDatabase, TimeProvider.System).InvalidateRecoveryAsync(deadline.Token);
                    }
                    break;
                case ["recovery-revalidate", "--email", var email, "--maintenance"]:
                    await operations.RevalidateAsync(email, false, deadline.Token); break;
                case ["recover-admin", "--email", var email, "--maintenance"]:
                    await operations.RevalidateAsync(email, true, deadline.Token); break;
                case ["qa-seed-catalog", "--count", var catalogRaw] when int.TryParse(catalogRaw, out var catalogCount):
                    var catalogOptions = new DbContextOptionsBuilder<Acropolis.Catalog.Infrastructure.CatalogDbContext>();
                    Acropolis.Catalog.Infrastructure.CatalogRegistration.ConfigureDatabase(catalogOptions, connection);
                    await using (var catalogDatabase = new Acropolis.Catalog.Infrastructure.CatalogDbContext(catalogOptions.Options))
                    {
                        catalogDatabase.Database.SetCommandTimeout(30);
                        await new CatalogOperations(catalogDatabase).SeedQaAsync(catalogCount, deadline.Token);
                    }
                    break;
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
