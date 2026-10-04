using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Acropolis.Platform.Infrastructure;

public sealed class PlatformMigrationRunner
{
    public const long AdvisoryLockKey = 719283401052L;

    public async Task RunAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = 3,
            CommandTimeout = 30,
            IncludeErrorDetail = false
        };
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var locked = false;
        try
        {
            while (!locked)
            {
                await using var attempt = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
                attempt.Parameters.AddWithValue("key", AdvisoryLockKey);
                locked = (bool)(await attempt.ExecuteScalarAsync(cancellationToken))!;
                if (!locked)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                }
            }

            await ApplyLockedAsync(connection, cancellationToken);
        }
        finally
        {
            if (locked && connection.State == System.Data.ConnectionState.Open)
            {
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                release.Parameters.AddWithValue("key", AdvisoryLockKey);
                await release.ExecuteScalarAsync(CancellationToken.None);
            }
        }
    }

    public static async Task ApplyLockedAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await ValidateSchemaAsync(connection, cancellationToken);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(connection, provider => provider.MigrationsHistoryTable(PlatformDbContext.HistoryTable, PlatformDbContext.Schema))
            .Options;
        await using var context = new PlatformDbContext(options);
        var expected = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
        if (applied.Any(migration => !expected.Contains(migration)))
        {
            throw new InvalidOperationException("The platform schema contains unsupported migrations.");
        }

        await context.Database.MigrateAsync(cancellationToken);
        await RestrictHistoryAsync(connection, cancellationToken);
    }

    private static async Task ValidateSchemaAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var owner = new NpgsqlCommand(
            "SELECT pg_has_role(current_user, nspowner, 'USAGE') FROM pg_namespace WHERE nspname = @schema", connection);
        owner.Parameters.AddWithValue("schema", PlatformDbContext.Schema);
        var permitted = await owner.ExecuteScalarAsync(cancellationToken);
        if (permitted is bool allowed && !allowed)
        {
            throw new InvalidOperationException("The migration role does not own the platform schema.");
        }

        await using var collision = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @schema AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
                  AND c.relname <> @history
            ) AND to_regclass('platform."__EFMigrationsHistory"') IS NULL
            """, connection);
        collision.Parameters.AddWithValue("schema", PlatformDbContext.Schema);
        collision.Parameters.AddWithValue("history", PlatformDbContext.HistoryTable);
        if ((bool)(await collision.ExecuteScalarAsync(cancellationToken))!)
        {
            throw new InvalidOperationException("The platform schema contains objects without migration history.");
        }
    }

    private static async Task RestrictHistoryAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var role = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_roles WHERE rolname = 'acropolis_app')", connection);
        if ((bool)(await role.ExecuteScalarAsync(cancellationToken))!)
        {
            await using var grants = new NpgsqlCommand(
                """
                REVOKE ALL ON TABLE platform."__EFMigrationsHistory" FROM acropolis_app;
                GRANT SELECT ON TABLE platform."__EFMigrationsHistory" TO acropolis_app;
                """, connection);
            await grants.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
