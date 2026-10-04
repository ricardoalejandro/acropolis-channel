using System.Collections.Concurrent;
using System.Net;
using Acropolis.Platform.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Acropolis.Api.Tests;

public sealed class ReadinessDiagnosticsTests
{
    [Theory]
    [InlineData("postgres", "database_error", "PostgresException", "53300")]
    [InlineData("wrapped", "database_error", "PostgresException", "53300")]
    [InlineData("invalid_state", "database_error", "PostgresException", "")]
    [InlineData("transport", "database_error", "NpgsqlException", "")]
    [InlineData("timeout", "timeout", "TimeoutException", "")]
    [InlineData("cancelled", "timeout", "OperationCanceledException", "")]
    [InlineData("unexpected", "probe_error", "InvalidOperationException", "")]
    [InlineData("other", "probe_error", "Exception", "")]
    public async Task ProbeFailuresLogOnlyWhitelistedMetadataAndRemain503(string kind, string reason, string type, string state)
    {
        var postgres = new PostgresException("private connection details", "ERROR", "ERROR", "53300", detail: "private password");
        Exception failure = kind switch
        {
            "postgres" => postgres,
            "invalid_state" => new PostgresException("private connection", "ERROR", "ERROR", "secret-password"),
            "wrapped" => new InvalidOperationException("private wrapper", postgres),
            "transport" => new NpgsqlException("private host"),
            "timeout" => new TimeoutException("private timeout"),
            "cancelled" => new OperationCanceledException("private cancellation"),
            "unexpected" => new InvalidOperationException("private unexpected"),
            _ => new IOException("private path")
        };
        await using var factory = new DiagnosticFactory(new Reader(_ => Task.FromException<bool>(failure)));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("""{"status":"not_ready"}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var recorded = Assert.Single(factory.Logs.Events);
        Assert.Equal("DatabaseReadinessFailure", recorded.Event.Name);
        Assert.Equal(reason, recorded.State["Reason"]);
        Assert.Equal(type, recorded.State["ExceptionType"]);
        Assert.Equal(state, recorded.State["SqlState"]);
        Assert.Null(recorded.Exception);
        Assert.DoesNotContain("private", recorded.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "ExceptionType", "Reason", "SqlState", "{OriginalFormat}" }, recorded.State.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ADeadlineIsDiagnosedWhileSuccessfulProbesRemainQuiet()
    {
        await using var factory = new DiagnosticFactory(new Reader(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        }));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal("timeout", Assert.Single(factory.Logs.Events).State["Reason"]);
        await using var healthy = new DiagnosticFactory(new Reader(_ => Task.FromResult(true)));
        using var healthyClient = healthy.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await healthyClient.GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Empty(healthy.Logs.Events);
    }

    private sealed class DiagnosticFactory(IPlatformStateReader reader) : WebApplicationFactory<Program>
    {
        public CaptureLogs Logs { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = "Host=127.0.0.1;Port=1;Database=acropolis_test_diagnostics;Username=unused",
                ["Identity:EmailEnabled"] = "false"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPlatformStateReader>();
                services.AddSingleton(reader);
                services.AddSingleton<ILoggerProvider>(Logs);
            });
        }
    }
    private sealed class Reader(Func<CancellationToken, Task<bool>> read) : IPlatformStateReader
    {
        public Task<bool> IsCurrentAsync(CancellationToken cancellationToken) => read(cancellationToken);
    }
    private sealed record Recorded(EventId Event, IReadOnlyDictionary<string, object?> State, Exception? Exception, string Message);
    private sealed class CaptureLogs : ILoggerProvider
    {
        public ConcurrentQueue<Recorded> Events { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Events);
        public void Dispose() { }
        private sealed class CaptureLogger(string category, ConcurrentQueue<Recorded> events) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == "Acropolis.Api.ReadinessDiagnostics" && eventId.Id == 1001)
                    events.Enqueue(new Recorded(eventId, ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(pair => pair.Key, pair => pair.Value), exception, formatter(state, exception)));
            }
        }
    }
}
