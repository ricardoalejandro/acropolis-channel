using System.Data.Common;
using Acropolis.Catalog.Infrastructure;
using Acropolis.Identity.Infrastructure;
using Acropolis.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Acropolis.Identity.IntegrationTests;

// This collection has no PostgreSQL fixture. Environment mutation is exclusive
// with every other collection in this assembly, including IdentityPostgres.
[CollectionDefinition("IdentitySeedGuardEnvironment", DisableParallelization = true)]
public sealed class IdentitySeedGuardCollection;

[Collection("IdentitySeedGuardEnvironment")]
public sealed class IdentitySeedGuardTests
{
    private const string Password = "Synthetic QA seed password 2026";
    private const string DatabaseName = "acropolis_test_seed_guard";

    [Theory]
    [InlineData(null)]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("testing")]
    public Task NonTestingEnvironmentRejectsSeedBeforeDatabaseAccess(string? environment) =>
        RejectBothBeforeDatabaseAccess(environment, DatabaseName, 1);

    [Theory]
    [InlineData("acropolis")]
    [InlineData("postgres")]
    [InlineData("acropolis_test_")]
    [InlineData("ACROPOLIS_TEST_guard")]
    [InlineData("acropolis_test_invalid-name")]
    [InlineData("acropolis_test_invalid.name")]
    [InlineData("acropolis_test_invalid name")]
    [InlineData("acropolis_test_guard\n")]
    [InlineData("acropolis_test_guard\r\n")]
    public Task ProductionOrInvalidDatabaseNameRejectsSeedEvenUnderTesting(string databaseName) =>
        RejectBothBeforeDatabaseAccess("Testing", databaseName, 1);

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(100001)]
    [InlineData(int.MaxValue)]
    public Task InvalidCountRejectsSeedBeforeDatabaseAccess(int count) =>
        RejectBothBeforeDatabaseAccess("Testing", DatabaseName, count);

    [Theory]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(129)]
    public Task InvalidPasswordLengthRejectsSeedBeforeDatabaseAccess(int length) =>
        RejectBeforeDatabaseAccess("Testing", DatabaseName, 1, new string('q', length));

    private static async Task RejectBothBeforeDatabaseAccess(string? environment, string databaseName, int count)
    {
        await RejectBeforeDatabaseAccess(environment, databaseName, count, Password);
        await RejectBeforeDatabaseAccess(environment, databaseName, count, Password, catalog: true);
    }

    private static async Task RejectBeforeDatabaseAccess(string? environment, string databaseName, int count, string password, bool catalog = false)
    {
        var previousEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", environment);
            var connectionGuard = new RejectConnectionOpening();
            var commandGuard = new RejectCommandCreation();
            var settings = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = 1,
                Username = "synthetic_seed_guard",
                Pooling = false,
                Timeout = 1,
                CommandTimeout = 1
            };
            // Keep invalid whitespace intact when preparing the value under test.
            var quotedDatabaseName = databaseName.Replace("\"", "\"\"", StringComparison.Ordinal);
            var connectionString = $"{settings.ConnectionString};Database=\"{quotedDatabaseName}\"";
            Assert.Equal(databaseName, new NpgsqlConnectionStringBuilder(connectionString).Database);
            if (catalog)
            {
                var options = new DbContextOptionsBuilder<CatalogDbContext>();
                CatalogRegistration.ConfigureDatabase(options, connectionString);
                options.AddInterceptors(connectionGuard, commandGuard);
                await using var database = new CatalogDbContext(options.Options);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new CatalogOperations(database).SeedQaAsync(count, TestContext.Current.CancellationToken));
            }
            else
            {
                var options = new DbContextOptionsBuilder<IdentityDbContext>();
                IdentityRegistration.ConfigureDatabase(options, connectionString);
                options.AddInterceptors(connectionGuard, commandGuard);
                await using var database = new IdentityDbContext(options.Options);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new IdentityOperations(database, TimeProvider.System).SeedQaAsync(count, password, TestContext.Current.CancellationToken));
            }
            Assert.Equal(0, connectionGuard.Attempts);
            Assert.Equal(0, commandGuard.Attempts);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousEnvironment);
        }
    }

    private sealed class RejectConnectionOpening : DbConnectionInterceptor
    {
        public int Attempts { get; private set; }
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result) => throw Block();
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default) => throw Block();
        private UnexpectedDatabaseAccessException Block()
        {
            Attempts++;
            return new();
        }
    }

    private sealed class RejectCommandCreation : DbCommandInterceptor
    {
        public int Attempts { get; private set; }
        // Command creation precedes reader, scalar and non-query execution, sync or async.
        public override InterceptionResult<DbCommand> CommandCreating(CommandCorrelatedEventData eventData,
            InterceptionResult<DbCommand> result)
        {
            Attempts++;
            throw new UnexpectedDatabaseAccessException();
        }
    }

    private sealed class UnexpectedDatabaseAccessException() : Exception("The QA seed guard attempted database access.");
}
