using System.Collections.Frozen;
using Acropolis.Api;
using Acropolis.Catalog.Infrastructure;
using Acropolis.Subscriptions.Infrastructure;
using Acropolis.Identity.Infrastructure;
using Acropolis.Platform.Application;
using Acropolis.Platform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

internal sealed class CompositeDatabaseReader : IPlatformStateReader
{
    private const string HistoryQuery = """
        SELECT 'platform', "MigrationId" FROM platform."__EFMigrationsHistory"
        UNION ALL
        SELECT 'identity', "MigrationId" FROM identity."__EFMigrationsHistory"
        UNION ALL
        SELECT 'catalog', "MigrationId" FROM catalog."__EFMigrationsHistory"
        UNION ALL
        SELECT 'subscriptions', "MigrationId" FROM subscriptions."__EFMigrationsHistory"
        """;
    private readonly NpgsqlDataSource dataSource;
    private readonly ReadinessDiagnostics diagnostics;
    private readonly FrozenSet<(string Module, string Migration)> expected;
    private readonly bool completeMetadata;

    public CompositeDatabaseReader(NpgsqlDataSource dataSource, ReadinessDiagnostics diagnostics)
    {
        this.dataSource = dataSource;
        this.diagnostics = diagnostics;
        var platformOptions = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(dataSource,
            provider => provider.MigrationsHistoryTable(PlatformDbContext.HistoryTable, PlatformDbContext.Schema));
        using var platform = new PlatformDbContext(platformOptions.Options);
        var identityOptions = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityRegistration.ConfigureDatabase(identityOptions, dataSource);
        using var identity = new IdentityDbContext(identityOptions.Options);
        var catalogOptions = new DbContextOptionsBuilder<CatalogDbContext>();
        CatalogRegistration.ConfigureDatabase(catalogOptions, dataSource);
        using var catalog = new CatalogDbContext(catalogOptions.Options);
        var subscriptionOptions = new DbContextOptionsBuilder<SubscriptionsDbContext>();
        SubscriptionsRegistration.ConfigureDatabase(subscriptionOptions, dataSource);
        using var subscriptions = new SubscriptionsDbContext(subscriptionOptions.Options);
        var histories = new[]
        {
            (Module: PlatformDbContext.Schema, Ids: platform.Database.GetMigrations().ToArray()),
            (Module: IdentityDbContext.Schema, Ids: identity.Database.GetMigrations().ToArray()),
            (Module: CatalogDbContext.Schema, Ids: catalog.Database.GetMigrations().ToArray()),
            (Module: SubscriptionsDbContext.Schema, Ids: subscriptions.Database.GetMigrations().ToArray())
        };
        completeMetadata = histories.All(history => history.Ids.Length > 0);
        expected = histories.SelectMany(history => history.Ids.Select(id => (history.Module, id))).ToFrozenSet();
    }

    public async Task<bool> IsCurrentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!completeMetadata)
        {
            diagnostics.HistoryMismatch();
            return false;
        }
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(HistoryQuery, connection);
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        var applied = new HashSet<(string Module, string Migration)>();
        while (await rows.ReadAsync(cancellationToken))
        {
            if (!applied.Add((rows.GetString(0), rows.GetString(1))))
            {
                diagnostics.HistoryMismatch();
                return false;
            }
        }
        if (applied.SetEquals(expected)) return true;
        diagnostics.HistoryMismatch();
        return false;
    }
}
