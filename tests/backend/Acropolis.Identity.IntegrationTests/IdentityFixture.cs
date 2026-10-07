using Acropolis.Identity.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Acropolis.Migrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Acropolis.Identity.IntegrationTests;

[CollectionDefinition("IdentityPostgres", DisableParallelization = true)]
public sealed class IdentityCollection : ICollectionFixture<IdentityFixture>;
public sealed class IdentityFixture : IAsyncLifetime
{
    public string AdminConnection { get; private set; } = "";
    public string RuntimeConnection => Role("acropolis_app");
    public string MigrationConnection => Role("acropolis_migrator");
    public async ValueTask InitializeAsync()
    {
        var raw = Environment.GetEnvironmentVariable("TEST_DATABASE_CONNECTION");
        if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") != "Testing" || string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Explicit Testing PostgreSQL configuration is required.");
        var settings = new NpgsqlConnectionStringBuilder(raw) { Pooling = false, IncludeErrorDetail = false };
        if (settings.Database is null || !System.Text.RegularExpressions.Regex.IsMatch(settings.Database, "^acropolis_test_[a-z0-9_]+$")) throw new InvalidOperationException("Identity tests require an isolated acropolis_test_* database.");
        AdminConnection = settings.ConnectionString;
        await ExecuteAsync("SELECT 1", TestContext.Current.CancellationToken);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public async Task ResetAsync(CancellationToken token)
    {
        await ExecuteAsync("DROP SCHEMA IF EXISTS subscriptions CASCADE; DROP SCHEMA IF EXISTS catalog CASCADE; DROP SCHEMA IF EXISTS identity CASCADE; DROP SCHEMA IF EXISTS platform CASCADE; CREATE SCHEMA platform AUTHORIZATION acropolis_migrator; GRANT USAGE ON SCHEMA platform TO acropolis_app; ALTER DEFAULT PRIVILEGES FOR ROLE acropolis_migrator IN SCHEMA platform GRANT SELECT,INSERT,UPDATE,DELETE ON TABLES TO acropolis_app", token);
        await new ChannelMigrationRunner().RunAsync(MigrationConnection, token);
    }
    public IdentityDbContext Context(bool migration = false)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityRegistration.ConfigureDatabase(options, migration ? MigrationConnection : RuntimeConnection);
        return new(options.Options);
    }
    public async Task ExecuteAsync(string sql, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }
    private string Role(string role) => new NpgsqlConnectionStringBuilder(AdminConnection) { Options = "-c role=" + role }.ConnectionString;
}
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
}
public sealed class CaptureMailer : IIdentityMailer
{
    public List<MailPayload> Messages { get; } = [];
    public bool Fail { get; set; }
    public Task SendAsync(MailPayload payload, CancellationToken token)
    {
        if (Fail) throw new InvalidOperationException("Synthetic SMTP outage");
        Messages.Add(payload);
        return Task.CompletedTask;
    }
}
public sealed class IdentityApiFactory(IdentityFixture fixture, MutableTicketDatabase? ticketDatabase = null) : WebApplicationFactory<Program>
{
    public TestClock Clock { get; } = new();
    public CaptureMailer Mailer { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = fixture.RuntimeConnection }));
        builder.ConfigureTestServices(services =>
        {
            var worker = services.SingleOrDefault(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(OutboxWorker));
            if (worker is not null) services.Remove(worker);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<IIdentityMailer>();
            services.AddSingleton<IIdentityMailer>(Mailer);
            if (ticketDatabase is not null)
            {
                services.RemoveAll<ITicketStore>();
                services.AddSingleton<ITicketStore>(provider => new PostgresTicketStore(ticketDatabase,
                    provider.GetRequiredService<IDataProtectionProvider>(), Clock));
            }
        });
    }
    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    public async Task DispatchAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync(TestContext.Current.CancellationToken);
    }
}


public sealed class MutableTicketDatabase(IdentityFixture fixture) : IDbContextFactory<IdentityDbContext>
{
    public bool Unavailable { get; set; }
    public bool UnexpectedFailure { get; set; }
    public IdentityDbContext CreateDbContext()
    {
        if (UnexpectedFailure) throw new InvalidOperationException("Synthetic failure outside PostgreSQL.");
        var connection = new NpgsqlConnectionStringBuilder(fixture.RuntimeConnection);
        if (Unavailable)
        {
            connection.Host = "127.0.0.1";
            connection.Port = 1;
            connection.Timeout = 1;
            connection.CommandTimeout = 1;
        }
        var options = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityRegistration.ConfigureDatabase(options, connection.ConnectionString);
        return new IdentityDbContext(options.Options);
    }
}
