using Acropolis.Catalog.Infrastructure;
using Acropolis.Identity.Infrastructure;
using Acropolis.Platform.Application;
using Acropolis.Platform.Infrastructure;
using Microsoft.EntityFrameworkCore;

internal sealed class CompositeDatabaseReader(string connectionString) : IPlatformStateReader
{
    public async Task<bool> IsCurrentAsync(CancellationToken cancellationToken)
    {
        if (!await new PostgresPlatformStateReader(connectionString).IsCurrentAsync(cancellationToken)) return false;
        var options = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityRegistration.ConfigureDatabase(options, connectionString);
        await using var identity = new IdentityDbContext(options.Options);
        var expected = identity.Database.GetMigrations().Order(StringComparer.Ordinal).ToArray();
        var applied = (await identity.Database.GetAppliedMigrationsAsync(cancellationToken)).Order(StringComparer.Ordinal).ToArray();
        if (expected.Length == 0 || !expected.SequenceEqual(applied, StringComparer.Ordinal)) return false;
        var catalogOptions = new DbContextOptionsBuilder<CatalogDbContext>();
        CatalogRegistration.ConfigureDatabase(catalogOptions, connectionString);
        await using var catalog = new CatalogDbContext(catalogOptions.Options);
        var catalogExpected = catalog.Database.GetMigrations().Order(StringComparer.Ordinal).ToArray();
        var catalogApplied = (await catalog.Database.GetAppliedMigrationsAsync(cancellationToken)).Order(StringComparer.Ordinal).ToArray();
        return catalogExpected.Length > 0 && catalogExpected.SequenceEqual(catalogApplied, StringComparer.Ordinal);
    }
}
