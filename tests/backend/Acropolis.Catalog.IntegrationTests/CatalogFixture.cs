using Acropolis.Catalog.Infrastructure;
using Acropolis.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Acropolis.Catalog.IntegrationTests;

[CollectionDefinition("CatalogPostgres", DisableParallelization = true)]
public sealed class CatalogCollection : ICollectionFixture<CatalogFixture>;
public sealed class CatalogFixture : IAsyncLifetime
{
    public string AdminConnection { get; private set; } = "";
    public string RuntimeConnection => new NpgsqlConnectionStringBuilder(AdminConnection) { Options = "-c role=acropolis_app" }.ConnectionString;
    public string MigrationConnection => new NpgsqlConnectionStringBuilder(AdminConnection) { Options = "-c role=acropolis_migrator" }.ConnectionString;
    public async ValueTask InitializeAsync()
    {
        var raw = Environment.GetEnvironmentVariable("TEST_DATABASE_CONNECTION");
        if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") != "Testing" || string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Explicit guarded Testing PostgreSQL configuration is required.");
        var settings = new NpgsqlConnectionStringBuilder(raw) { Pooling = false, IncludeErrorDetail = false };
        if (settings.Database is null || !System.Text.RegularExpressions.Regex.IsMatch(settings.Database, "^acropolis_test_[a-z0-9_]+$")) throw new InvalidOperationException("Catalog tests require an acropolis_test_* database.");
        AdminConnection = settings.ConnectionString;
        await ExecuteAsync("SELECT 1", TestContext.Current.CancellationToken);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public async Task ResetAsync(CancellationToken token)
    {
        await ExecuteAsync("DROP SCHEMA IF EXISTS subscriptions CASCADE; DROP SCHEMA IF EXISTS catalog CASCADE; DROP SCHEMA IF EXISTS identity CASCADE; DROP SCHEMA IF EXISTS platform CASCADE; CREATE SCHEMA platform AUTHORIZATION acropolis_migrator; GRANT USAGE ON SCHEMA platform TO acropolis_app; ALTER DEFAULT PRIVILEGES FOR ROLE acropolis_migrator IN SCHEMA platform GRANT SELECT,INSERT,UPDATE,DELETE ON TABLES TO acropolis_app", token);
        await new ChannelMigrationRunner().RunAsync(MigrationConnection, token);
    }
    public CatalogDbContext Context(bool migration = false, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>();
        CatalogRegistration.ConfigureDatabase(options, migration ? MigrationConnection : RuntimeConnection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }
    public async Task ExecuteAsync(string sql, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(AdminConnection); await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync(token);
    }
}
public sealed class CatalogClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance() => now = now.AddSeconds(1);
}
