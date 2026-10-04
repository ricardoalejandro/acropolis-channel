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
        return expected.Length > 0 && expected.SequenceEqual(applied, StringComparer.Ordinal);
    }
}
