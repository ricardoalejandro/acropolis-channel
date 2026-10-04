using Acropolis.Platform.Application;
using Microsoft.EntityFrameworkCore;

namespace Acropolis.Platform.Infrastructure;

public sealed class PostgresPlatformStateReader(string connectionString) : IPlatformStateReader
{
    public async Task<bool> IsCurrentAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        await using var context = new PlatformDbContext(PlatformDbContext.CreateOptions(connectionString));
        if (!await context.Database.CanConnectAsync(cancellationToken))
        {
            return false;
        }

        var expected = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToArray();
        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken))
            .Order(StringComparer.Ordinal).ToArray();
        return expected.Length > 0 && expected.SequenceEqual(applied, StringComparer.Ordinal);
    }
}
