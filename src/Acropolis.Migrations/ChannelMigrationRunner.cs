using Acropolis.Catalog.Infrastructure;
using Acropolis.Subscriptions.Infrastructure;
using Acropolis.Identity.Infrastructure;
using Acropolis.Platform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Acropolis.Migrations;

public sealed class ChannelMigrationRunner
{
    public async Task RunAsync(string connectionString, CancellationToken token = default)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 3, CommandTimeout = 30, IncludeErrorDetail = false };
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        var locked = false;
        try
        {
            while (!locked)
            {
                await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
                acquire.Parameters.AddWithValue("key", PlatformMigrationRunner.AdvisoryLockKey);
                locked = (bool)(await acquire.ExecuteScalarAsync(token))!;
                if (!locked) await Task.Delay(200, token);
            }
            await PlatformMigrationRunner.ApplyLockedAsync(connection, token);
            await ValidateIdentitySchemaAsync(connection, token);
            await using (var grants = new NpgsqlCommand(
                """
                CREATE SCHEMA IF NOT EXISTS identity AUTHORIZATION acropolis_migrator;
                GRANT USAGE ON SCHEMA identity TO acropolis_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE acropolis_migrator IN SCHEMA identity GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO acropolis_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE acropolis_migrator IN SCHEMA identity GRANT USAGE, SELECT ON SEQUENCES TO acropolis_app;
                """, connection))
                await grants.ExecuteNonQueryAsync(token);
            var options = new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(connection, provider => provider.MigrationsHistoryTable(IdentityDbContext.HistoryTable, IdentityDbContext.Schema));
            await using var context = new IdentityDbContext(options.Options);
            var expected = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            if ((await context.Database.GetAppliedMigrationsAsync(token)).Any(id => !expected.Contains(id)))
                throw new InvalidOperationException("The identity schema contains unsupported migrations.");
            await context.Database.MigrateAsync(token);
            await using var privileges = await connection.BeginTransactionAsync(token);
            await using var restrict = new NpgsqlCommand(
                """
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity TO acropolis_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA identity TO acropolis_app;
                REVOKE ALL ON TABLE identity."__EFMigrationsHistory" FROM acropolis_app;
                GRANT SELECT ON TABLE identity."__EFMigrationsHistory" TO acropolis_app;
                REVOKE UPDATE, DELETE ON TABLE identity."Audit" FROM acropolis_app;
                REVOKE INSERT, UPDATE, DELETE ON TABLE identity."Bootstrap" FROM acropolis_app;
                """, connection, privileges);
            await restrict.ExecuteNonQueryAsync(token);
            await privileges.CommitAsync(token);
            await ApplyCatalogAsync(connection, token);
            await ApplySubscriptionsAsync(connection, token);
        }
        finally
        {
            if (locked && connection.State == System.Data.ConnectionState.Open)
            {
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                release.Parameters.AddWithValue("key", PlatformMigrationRunner.AdvisoryLockKey);
                await release.ExecuteScalarAsync(CancellationToken.None);
            }
        }
    }
    private static async Task ApplyCatalogAsync(NpgsqlConnection connection, CancellationToken token)
    {
        await using (var owner = new NpgsqlCommand("SELECT pg_has_role(current_user,nspowner,'USAGE') FROM pg_namespace WHERE nspname='catalog'", connection))
            if (await owner.ExecuteScalarAsync(token) is bool permitted && !permitted)
                throw new InvalidOperationException("The migration role does not own the catalog schema.");
        await using (var collision = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='catalog' AND c.relkind IN ('r','p','v','m','S','f'))
            AND to_regclass('catalog."__EFMigrationsHistory"') IS NULL
            """, connection))
            if ((bool)(await collision.ExecuteScalarAsync(token))!) throw new InvalidOperationException("The catalog schema contains objects without migration history.");
        await using (var grants = new NpgsqlCommand("""
            CREATE SCHEMA IF NOT EXISTS catalog AUTHORIZATION acropolis_migrator;
            GRANT USAGE ON SCHEMA catalog TO acropolis_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE acropolis_migrator IN SCHEMA catalog GRANT SELECT,INSERT,UPDATE,DELETE ON TABLES TO acropolis_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE acropolis_migrator IN SCHEMA catalog GRANT USAGE,SELECT ON SEQUENCES TO acropolis_app;
            """, connection))
            await grants.ExecuteNonQueryAsync(token);
        var options = new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(connection, provider => provider.MigrationsHistoryTable(CatalogDbContext.HistoryTable, CatalogDbContext.Schema));
        await using var context = new CatalogDbContext(options.Options);
        var expected = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        if ((await context.Database.GetAppliedMigrationsAsync(token)).Any(id => !expected.Contains(id)))
            throw new InvalidOperationException("The catalog schema contains unsupported migrations.");
        await context.Database.MigrateAsync(token);
        await using var privileges = await connection.BeginTransactionAsync(token);
        await using var restrict = new NpgsqlCommand("""
            GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA catalog TO acropolis_app;
            GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA catalog TO acropolis_app;
            REVOKE ALL ON TABLE catalog."__EFMigrationsHistory" FROM acropolis_app;
            GRANT SELECT ON TABLE catalog."__EFMigrationsHistory" TO acropolis_app;
            REVOKE UPDATE,DELETE ON TABLE catalog."Audit" FROM acropolis_app;
            REVOKE DELETE ON TABLE catalog."Contents", catalog."Topics", catalog."TopicDirectory" FROM acropolis_app;
            REVOKE UPDATE,DELETE ON TABLE catalog."TopicAudit" FROM acropolis_app;
            REVOKE UPDATE ON TABLE catalog."ConsumptionPulses" FROM acropolis_app;
            """, connection, privileges);
        await restrict.ExecuteNonQueryAsync(token);
        await privileges.CommitAsync(token);
    }

    private static async Task ApplySubscriptionsAsync(NpgsqlConnection connection, CancellationToken token)
    {
        await using (var owner = new NpgsqlCommand("SELECT pg_has_role(current_user,nspowner,'USAGE') FROM pg_namespace WHERE nspname='subscriptions'", connection))
            if (await owner.ExecuteScalarAsync(token) is bool permitted && !permitted)
                throw new InvalidOperationException("The migration role does not own the subscriptions schema.");
        await using (var collision = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='subscriptions' AND c.relkind IN ('r','p','v','m','S','f'))
            AND to_regclass('subscriptions."__EFMigrationsHistory"') IS NULL
            """, connection))
            if ((bool)(await collision.ExecuteScalarAsync(token))!) throw new InvalidOperationException("The subscriptions schema contains objects without migration history.");
        await using (var grants = new NpgsqlCommand("""
            CREATE SCHEMA IF NOT EXISTS subscriptions AUTHORIZATION acropolis_migrator;
            GRANT USAGE ON SCHEMA subscriptions TO acropolis_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE acropolis_migrator IN SCHEMA subscriptions GRANT SELECT,INSERT,UPDATE,DELETE ON TABLES TO acropolis_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE acropolis_migrator IN SCHEMA subscriptions GRANT USAGE,SELECT ON SEQUENCES TO acropolis_app;
            """, connection))
            await grants.ExecuteNonQueryAsync(token);
        var options = new DbContextOptionsBuilder<SubscriptionsDbContext>().UseNpgsql(connection, provider => provider.MigrationsHistoryTable(SubscriptionsDbContext.HistoryTable, SubscriptionsDbContext.Schema));
        await using var context = new SubscriptionsDbContext(options.Options);
        var expected = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        if ((await context.Database.GetAppliedMigrationsAsync(token)).Any(id => !expected.Contains(id)))
            throw new InvalidOperationException("The subscriptions schema contains unsupported migrations.");
        await context.Database.MigrateAsync(token);
        await using var privileges = await connection.BeginTransactionAsync(token);
        await using var restrict = new NpgsqlCommand("""
            GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA subscriptions TO acropolis_app;
            GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA subscriptions TO acropolis_app;
            REVOKE ALL ON TABLE subscriptions."__EFMigrationsHistory" FROM acropolis_app;
            GRANT SELECT ON TABLE subscriptions."__EFMigrationsHistory" TO acropolis_app;
            REVOKE UPDATE,DELETE ON TABLE subscriptions."Audit" FROM acropolis_app;
            REVOKE DELETE ON TABLE subscriptions."Subscriptions" FROM acropolis_app;
            """, connection, privileges);
        await restrict.ExecuteNonQueryAsync(token);
        await privileges.CommitAsync(token);
    }

    private static async Task ValidateIdentitySchemaAsync(NpgsqlConnection connection, CancellationToken token)
    {
        await using var owner = new NpgsqlCommand("SELECT pg_has_role(current_user,nspowner,'USAGE') FROM pg_namespace WHERE nspname='identity'", connection);
        if (await owner.ExecuteScalarAsync(token) is bool permitted && !permitted) throw new InvalidOperationException("The migration role does not own the identity schema.");
        await using var collision = new NpgsqlCommand(
            """
            SELECT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='identity' AND c.relkind IN ('r','p','v','m','S','f'))
            AND to_regclass('identity."__EFMigrationsHistory"') IS NULL
            """, connection);
        if ((bool)(await collision.ExecuteScalarAsync(token))!) throw new InvalidOperationException("The identity schema contains objects without migration history.");
    }
}
