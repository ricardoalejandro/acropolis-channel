using Acropolis.Platform.Infrastructure;

namespace Acropolis.Migrations;

internal static class EntryPoint
{
    private static async Task<int> Main()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Database");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("{\"error\":\"database_configuration_missing\"}");
            return 1;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await new PlatformMigrationRunner().RunAsync(connectionString, deadline.Token);
            Console.WriteLine("{\"status\":\"migrations_current\"}");
            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("{\"error\":\"migration_failed\"}");
            return 1;
        }
    }
}
