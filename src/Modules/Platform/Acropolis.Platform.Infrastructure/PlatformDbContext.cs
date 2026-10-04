using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Acropolis.Platform.Infrastructure;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public const string Schema = "platform";
    public const string HistoryTable = "__EFMigrationsHistory";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
    }

    public static DbContextOptions<PlatformDbContext> CreateOptions(string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = 3,
            CommandTimeout = 3,
            IncludeErrorDetail = false
        };
        return new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(settings.ConnectionString, provider => provider.MigrationsHistoryTable(HistoryTable, Schema))
            .Options;
    }
}
