using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Acropolis.Identity.Application;
using Acropolis.Subscriptions.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acropolis.Api.Tests;

// Real host, policies and serialization; aggregate counts are proven separately with PostgreSQL.
[Collection("SubscriptionEventReportsHttpTests")]
public sealed class SubscriptionEventReportsHttpTests
{
    private const string Route = "/api/v1/admin/reports/subscriptions/events";
    private const string ValidQuery = "?from=2026-10-01&to=2026-10-03";
    private static readonly DateTimeOffset Snapshot = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("")]
    [InlineData("?from=2026-10-01&to=2026-10-03")]
    [InlineData("?from=invalid&to=2026-10-03")]
    public async Task AnonymousRequestsAreRejectedBeforeValidationOrReportWork(string query)
    {
        await using var factory = new EventFactory(authenticated: false);
        using var client = Client(factory);
        using var response = await client.GetAsync(Route + query, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        PrivateHeaders(response);
        Assert.Equal(0, factory.Events.Calls);
        Assert.Equal(0, factory.Current.Calls);
    }

    [Theory]
    [InlineData(IdentityRules.ManageSubscriptions, false)]
    [InlineData(IdentityRules.ManageUsers, true)]
    [InlineData(IdentityRules.ManageContent, true)]
    [InlineData("", true)]
    public async Task OnlyTheIndependentSubscriptionsPermissionWithMfaCanReadEvents(string permission, bool mfa)
    {
        await using var factory = new EventFactory(mfa: mfa, permission: permission);
        using var client = Client(factory);
        using var response = await client.GetAsync(Route + "?unknown=invalid", Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        PrivateHeaders(response);
        Assert.Equal(0, factory.Events.Calls);
        Assert.Equal(0, factory.Current.Calls);
    }

    [Collection("SubscriptionEventReportsHttpTests")]
    public sealed class InvalidQueryCases(InvalidQueryFixture factory) : IClassFixture<InvalidQueryFixture>
    {
        [Theory]
        [InlineData("")]
        [InlineData("?from=2026-10-01")]
        [InlineData("?to=2026-10-03")]
        [InlineData("?from=&to=2026-10-03")]
        [InlineData("?from=2026-10-01&to=")]
        [InlineData("?from=2026-10-01&from=2026-10-01&to=2026-10-03")]
        [InlineData("?from=2026-10-01&to=2026-10-03&to=2026-10-03")]
        [InlineData("?From=2026-10-01&to=2026-10-03")]
        [InlineData("?from=2026-10-01&To=2026-10-03")]
        [InlineData("?from=2026-10-01&to=2026-10-03&unknown=private-do-not-echo%40example.test")]
        [InlineData("?from=2026-10-01&to=2026-10-03&page=1")]
        [InlineData("?from=2026-10-01&to=2026-10-03&status=active")]
        [InlineData("?from=2026-10-01&to=2026-10-03&userId=10000000-0000-0000-0000-000000000001")]
        [InlineData("?from=2026-10-02&to=2026-10-02")]
        [InlineData("?from=2026-10-03&to=2026-10-02")]
        [InlineData("?from=2024-01-01&to=2025-01-02")]
        [InlineData("?from=2026-02-29&to=2026-03-01")]
        [InlineData("?from=2026-10-01T00%3A00%3A00Z&to=2026-10-03")]
        [InlineData("?from=%202026-10-01&to=2026-10-03")]
        [InlineData("?from=2026-10-01&to=2026-10-03%20")]
        public async Task AuthorizedQueriesMustContainExactlyTwoUniqueValidCalendarDates(string query)
        {
            using var client = factory.CreateClient();
            using var response = await client.GetAsync(Route + query, Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            PrivateHeaders(response);
            var body = await response.Content.ReadAsStringAsync(Token);
            using var problem = JsonDocument.Parse(body);
            Assert.Equal("validation_error", problem.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain("private-do-not-echo", body, StringComparison.Ordinal);
            Assert.DoesNotContain("example.test", body, StringComparison.Ordinal);
            Assert.Equal(0, factory.EventCalls);
            Assert.Equal(0, factory.CurrentCalls);
        }
    }

    public sealed class InvalidQueryFixture : IAsyncDisposable
    {
        private readonly EventFactory factory = new();
        public int EventCalls => factory.Events.Calls;
        public int CurrentCalls => factory.Current.Calls;
        public HttpClient CreateClient() => Client(factory);
        public ValueTask DisposeAsync() => factory.DisposeAsync();
    }

    [Theory]
    [InlineData("2026-10-01", "2026-10-02", 1)]
    [InlineData("2026-10-01", "2026-10-03", 2)]
    [InlineData("2024-01-01", "2025-01-01", 366)]
    public async Task SuccessReturnsOnlyTheExactEventAndDailyContract(string from, string to, int days)
    {
        await using var factory = new EventFactory();
        using var client = Client(factory);
        using var response = await client.GetAsync(Route + $"?from={from}&to={to}", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        PrivateHeaders(response);
        var body = await response.Content.ReadAsStringAsync(Token);
        using var document = JsonDocument.Parse(body);
        var report = document.RootElement;
        ExactProperties(report, "generatedUtc", "fromUtc", "toUtc", "totalEvents", "byEvent", "days", "scope");
        Assert.Equal("recorded_events", report.GetProperty("scope").GetString());
        Assert.Equal(Snapshot, report.GetProperty("generatedUtc").GetDateTimeOffset());
        Assert.Equal(Midnight(DateOnly.ParseExact(from, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)), report.GetProperty("fromUtc").GetDateTimeOffset());
        Assert.Equal(Midnight(DateOnly.ParseExact(to, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)), report.GetProperty("toUtc").GetDateTimeOffset());
        Assert.Equal(TimeSpan.Zero, report.GetProperty("fromUtc").GetDateTimeOffset().Offset);
        Assert.Equal(TimeSpan.Zero, report.GetProperty("toUtc").GetDateTimeOffset().Offset);
        Assert.Equal(3L, report.GetProperty("totalEvents").GetInt64());
        Groups(report.GetProperty("byEvent"), 2, 1);
        var rows = report.GetProperty("days");
        Assert.Equal(days, rows.GetArrayLength());
        for (var index = 0; index < days; index++)
        {
            var day = rows[index];
            ExactProperties(day, "dayUtc", "totalEvents", "byEvent");
            Assert.Equal(DateOnly.ParseExact(from, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).AddDays(index).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), day.GetProperty("dayUtc").GetString());
            var activated = index == 0 ? 2L : 0L;
            var suspended = index == (days == 1 ? 0 : 1) ? 1L : 0L;
            Assert.Equal(activated + suspended, day.GetProperty("totalEvents").GetInt64());
            Groups(day.GetProperty("byEvent"), activated, suspended);
        }
        Assert.Equal(3L, rows.EnumerateArray().Sum(day => day.GetProperty("totalEvents").GetInt64()));
        foreach (var field in new[] { "userId", "subscriptionId", "actorId", "email", "displayName", "reason", "version", "renewal" })
            Assert.DoesNotContain('"' + field + '"', body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new SubscriptionEventInterval(DateOnly.ParseExact(from, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), DateOnly.ParseExact(to, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)), factory.Events.Interval);
        Assert.True(factory.Events.ReceivedToken.CanBeCanceled);
        Assert.Equal(1, factory.Events.Calls);
        Assert.Equal(0, factory.Current.Calls);
    }

    [Fact]
    public async Task DatabaseFailureIsSanitizedAndAnExplicitRetryCanSucceed()
    {
        await using var factory = new EventFactory();
        factory.Events.Fail = true;
        using var client = Client(factory);
        using var response = await client.GetAsync(Route + ValidQuery, Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        PrivateHeaders(response);
        var body = await response.Content.ReadAsStringAsync(Token);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("service_unavailable", document.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("synthetic-private-event-connection", body, StringComparison.Ordinal);
        Assert.DoesNotContain("qa-only-details", body, StringComparison.Ordinal);
        Assert.Equal(1, factory.Events.Calls);
        factory.Events.Fail = false;
        using var retry = await client.GetAsync(Route + ValidQuery, Token);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(2, factory.Events.Calls);
        Assert.Equal(0, factory.Current.Calls);
    }

    [Fact]
    public async Task CancelledHttpRequestCancelsReportWorkAndAllowsAnExplicitRetry()
    {
        await using var factory = new EventFactory();
        factory.Events.Block = true;
        using var client = Client(factory);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = client.GetAsync(Route + ValidQuery, cancellation.Token);
        await factory.Events.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var response = await pending; });
        await factory.Events.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(factory.Events.ReceivedToken.IsCancellationRequested);
        Assert.Equal(1, factory.Events.Calls);
        factory.Events.Block = false;
        using var retry = await client.GetAsync(Route + ValidQuery, Token);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(2, factory.Events.Calls);
        Assert.Equal(0, factory.Current.Calls);
    }

    [Fact]
    public async Task CurrentSubscriptionSnapshotRetainsItsOwnServiceAndRejectsEventDates()
    {
        await using var factory = new EventFactory();
        using var client = Client(factory);
        using var current = await client.GetAsync("/api/v1/admin/reports/subscriptions", Token);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        using var document = JsonDocument.Parse(await current.Content.ReadAsStringAsync(Token));
        ExactProperties(document.RootElement, "generatedUtc", "total", "byStatus", "scope");
        Assert.Equal("current", document.RootElement.GetProperty("scope").GetString());
        Assert.Equal(1, factory.Current.Calls);
        using var invalid = await client.GetAsync("/api/v1/admin/reports/subscriptions" + ValidQuery, Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(1, factory.Current.Calls);
        Assert.Equal(0, factory.Events.Calls);
    }

    private static DateTimeOffset Midnight(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    private static HttpClient Client(EventFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    private static void PrivateHeaders(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }
    private static void ExactProperties(JsonElement value, params string[] names) => Assert.Equal(names.Order(StringComparer.Ordinal), value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    private static void Groups(JsonElement groups, long activated, long suspended)
    {
        Assert.Equal(11, groups.GetArrayLength());
        for (var index = 0; index < SubscriptionEventReportRules.Keys.Count; index++)
        {
            var row = groups[index];
            ExactProperties(row, "key", "count");
            var key = SubscriptionEventReportRules.Keys[index];
            Assert.Equal(key, row.GetProperty("key").GetString());
            Assert.Equal(key == "activated" ? activated : key == "updated_active_suspended" ? suspended : 0L, row.GetProperty("count").GetInt64());
        }
    }

    private sealed class EventFactory(bool authenticated = true, bool mfa = true, string permission = IdentityRules.ManageSubscriptions) : WebApplicationFactory<Program>
    {
        public EventSpy Events { get; } = new();
        public CurrentSpy Current { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = "Host=127.0.0.1;Port=1;Database=acropolis_test_event_http;Username=synthetic_event_http;Timeout=1;Command Timeout=1",
                ["Identity:EmailEnabled"] = "false", ["Identity:PublicOrigin"] = "https://localhost"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISubscriptionEventReportService>();
                services.RemoveAll<ISubscriptionReportService>();
                services.AddSingleton<ISubscriptionEventReportService>(Events);
                services.AddSingleton<ISubscriptionReportService>(Current);
                services.AddSingleton(new SyntheticSubject(authenticated, mfa, permission));
                services.AddAuthentication(options =>
                {
                    options.DefaultScheme = Authentication.SchemeName;
                    options.DefaultAuthenticateScheme = Authentication.SchemeName;
                    options.DefaultChallengeScheme = Authentication.SchemeName;
                    options.DefaultForbidScheme = Authentication.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, Authentication>(Authentication.SchemeName, _ => { });
            });
        }
    }
    private sealed record SyntheticSubject(bool Authenticated, bool Mfa, string Permission);
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, SyntheticSubject subject) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "qa-subscription-events";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!subject.Authenticated) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "10000000-0000-0000-0000-000000000001"), new("amr", subject.Mfa ? "mfa" : "pwd") };
            if (subject.Permission.Length > 0) claims.Add(new("permission", subject.Permission));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
        protected override Task HandleChallengeAsync(AuthenticationProperties properties) => IdentityEndpoints.Problem("invalid_credentials", 401).ExecuteAsync(Context);
        protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => IdentityEndpoints.Problem("forbidden", 403).ExecuteAsync(Context);
    }
    private sealed class EventSpy : ISubscriptionEventReportService
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public bool Fail { get; set; }
        public bool Block { get; set; }
        public SubscriptionEventInterval? Interval { get; private set; }
        public CancellationToken ReceivedToken { get; private set; }
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SubscriptionEventReportView> GetEventsAsync(SubscriptionEventInterval interval, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            Interval = interval; ReceivedToken = token;
            if (Fail) throw new Npgsql.NpgsqlException("synthetic-private-event-connection: qa-only-details");
            if (Block)
            {
                Entered.TrySetResult(true);
                try
                {
                    return await new TaskCompletionSource<SubscriptionEventReportView>(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Cancelled.TrySetResult(true);
                    throw;
                }
            }
            var days = interval.To.DayNumber - interval.From.DayNumber;
            var rows = Enumerable.Range(0, days).Select(index => new SubscriptionEventDay(interval.From.AddDays(index),
                (index == 0 ? 2L : 0L) + (index == (days == 1 ? 0 : 1) ? 1L : 0L),
                Counts(index == 0 ? 2 : 0, index == (days == 1 ? 0 : 1) ? 1 : 0))).ToArray();
            return new(Snapshot, Midnight(interval.From), Midnight(interval.To), 3, Counts(2, 1), rows);
        }
        private static SubscriptionEventCount[] Counts(long activated, long suspended) => SubscriptionEventReportRules.Keys.Select(key => new SubscriptionEventCount(key, key == "activated" ? activated : key == "updated_active_suspended" ? suspended : 0L)).ToArray();
    }
    private sealed class CurrentSpy : ISubscriptionReportService
    {
        public int Calls { get; private set; }
        public Task<SubscriptionReportView> GetCurrentAsync(CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new SubscriptionReportView(Snapshot, 0, [new("active", 0), new("cancelled", 0), new("suspended", 0)]));
        }
    }
}
