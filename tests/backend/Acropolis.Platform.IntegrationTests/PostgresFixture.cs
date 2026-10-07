using Npgsql;

namespace Acropolis.Platform.IntegrationTests;

[CollectionDefinition("Postgres", DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

public sealed class PostgresFixture : IAsyncLifetime
{
    public string AdminConnection { get; private set; } = string.Empty;
    public string MigrationConnection => WithRole("acropolis_migrator");
    public string RuntimeConnection => WithRole("acropolis_app");

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") != "Testing")
        {
            throw new InvalidOperationException("Real database tests require DOTNET_ENVIRONMENT=Testing.");
        }

        var raw = Environment.GetEnvironmentVariable("TEST_DATABASE_CONNECTION");
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("TEST_DATABASE_CONNECTION is required; database tests have no fallback.");
        }

        var settings = new NpgsqlConnectionStringBuilder(raw)
        {
            Pooling = false,
            Timeout = 3,
            CommandTimeout = 5,
            IncludeErrorDetail = false
        };
        if (settings.Database is null || !System.Text.RegularExpressions.Regex.IsMatch(settings.Database, "^acropolis_test_[a-z0-9_]+$"))
        {
            throw new InvalidOperationException("Database tests may only use an isolated acropolis_test_* database.");
        }
        AdminConnection = settings.ConnectionString;
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_roles WHERE rolname IN ('acropolis_migrator', 'acropolis_app')", connection);
        if ((long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))! != 2)
        {
            throw new InvalidOperationException("QA database must initialize the migrator and runtime roles.");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task ResetSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            DROP SCHEMA IF EXISTS subscriptions CASCADE;
            DROP SCHEMA IF EXISTS catalog CASCADE;
            DROP SCHEMA IF EXISTS identity CASCADE;
            DROP SCHEMA IF EXISTS platform CASCADE;
            CREATE SCHEMA platform AUTHORIZATION acropolis_migrator;
            GRANT USAGE ON SCHEMA platform TO acropolis_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE acropolis_migrator IN SCHEMA platform
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO acropolis_app;
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<T> ScalarAsync<T>(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task SetConnectionsAllowedAsync(bool allow, CancellationToken cancellationToken)
    {
        var settings = new NpgsqlConnectionStringBuilder(AdminConnection);
        var database = settings.Database!;
        settings.Database = "postgres";
        await using var control = new NpgsqlConnection(settings.ConnectionString);
        await control.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"ALTER DATABASE \"{database}\" ALLOW_CONNECTIONS {(allow ? "true" : "false")}", control);
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (!allow)
        {
            await using var terminate = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @database", control);
            terminate.Parameters.AddWithValue("database", database);
            await terminate.ExecuteNonQueryAsync(cancellationToken);
        }
        NpgsqlConnection.ClearAllPools();
    }

    private string WithRole(string role)
    {
        var settings = new NpgsqlConnectionStringBuilder(AdminConnection) { Options = "-c role=" + role };
        return settings.ConnectionString;
    }
}
