using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Acropolis.Catalog.Application;
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

// Tests the real host's policies and HTTP contract with synthetic authentication
// and aggregate services. PostgreSQL integration tests prove the actual counts.
public sealed class OperationalReportsHttpTests
{
    private static readonly DateTimeOffset SnapshotTime = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Modules = ["identity", "catalog", "subscriptions"];
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> AnonymousRoutes
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var module in Modules)
                foreach (var query in new[] { "", "?fromUtc=invalid" }) cases.Add(module, query);
            return cases;
        }
    }
    public static TheoryData<string> MatchingPermissions
    {
        get
        {
            var cases = new TheoryData<string>();
            foreach (var module in Modules) cases.Add(module);
            return cases;
        }
    }
    public static TheoryData<string, string> CrossedPermissions
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var module in Modules)
                foreach (var other in Modules.Where(other => other != module)) cases.Add(module, Permission(other));
            return cases;
        }
    }
    public static TheoryData<string, string> UnsupportedQueries
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var module in Modules)
                foreach (var query in new[]
                {
                    "fromUtc=invalid&toUtc=invalid",
                    "fromUtc=2026-10-07&toUtc=2026-10-06",
                    "fromUtc=2026-10-01&toUtc=2026-10-02",
                    "unknown=do-not-echo%40example.test"
                }) cases.Add(module, query);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(AnonymousRoutes))]
    public async Task AnonymousReportsAreUnauthorizedBeforeQueryValidationAndCannotBeCached(string module, string query)
    {
        await using var factory = new ReportFactory(module, authenticated: false);
        using var client = Client(factory);
        using var response = await client.GetAsync(Path(module) + query, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        PrivateHeaders(response);
        factory.Reports.AssertNoCalls();
    }

    [Theory]
    [MemberData(nameof(MatchingPermissions))]
    public async Task MatchingPermissionWithoutMfaIsForbiddenBeforeQueryValidation(string module)
    {
        await using var factory = new ReportFactory(module, mfa: false);
        using var client = Client(factory);
        using var response = await client.GetAsync(Path(module) + "?fromUtc=invalid", Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        PrivateHeaders(response);
        factory.Reports.AssertNoCalls();
    }

    [Theory]
    [MemberData(nameof(MatchingPermissions))]
    public async Task MfaWithoutTheRequiredPermissionCannotReadReports(string module)
    {
        await using var factory = new ReportFactory(module, permission: "");
        using var client = Client(factory);
        using var response = await client.GetAsync(Path(module), Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        PrivateHeaders(response);
        factory.Reports.AssertNoCalls();
    }

    [Theory]
    [MemberData(nameof(CrossedPermissions))]
    public async Task AdministrativePermissionsDoNotGrantAnotherModuleReport(string module, string permission)
    {
        await using var factory = new ReportFactory(module, permission: permission);
        using var client = Client(factory);
        using var response = await client.GetAsync(Path(module), Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        PrivateHeaders(response);
        factory.Reports.AssertNoCalls();
    }

    [Theory]
    [MemberData(nameof(UnsupportedQueries))]
    public async Task CurrentSnapshotRejectsAllUnsupportedQueriesWithoutInvokingAnyService(string module, string query)
    {
        await using var factory = new ReportFactory(module);
        using var client = Client(factory);
        using var response = await client.GetAsync(Path(module) + "?" + query, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        PrivateHeaders(response);
        var body = await response.Content.ReadAsStringAsync(Token);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("validation_error", problem.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("do-not-echo", body, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", body, StringComparison.Ordinal);
        factory.Reports.AssertNoCalls();
    }

    [Theory]
    [MemberData(nameof(MatchingPermissions))]
    public async Task PermittedMfaReportsReturnOnlyTheirModuleAggregateContract(string module)
    {
        await using var factory = new ReportFactory(module);
        using var client = Client(factory);
        using var response = await client.GetAsync(Path(module), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        PrivateHeaders(response);
        using var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var root = report.RootElement;
        Assert.Equal("current", root.GetProperty("scope").GetString());
        Assert.Equal(SnapshotTime, root.GetProperty("generatedUtc").GetDateTimeOffset());
        switch (module)
        {
            case "identity":
                ExactProperties(root, "generatedUtc", "total", "byStatus", "byLevel", "scope");
                Assert.Equal(5, root.GetProperty("total").GetInt64());
                CountGroups(root.GetProperty("byStatus"), ["active", "pending", "disabled"], [3, 1, 1]);
                CountGroups(root.GetProperty("byLevel"), ["Externo", "Probacionista", "Miembro", "FFVV", "Instructor", "Hachado"], [2, 1, 3, 0, 1, 0]);
                // Institutional levels coexist; their sum may exceed the account total.
                Assert.True(root.GetProperty("byLevel").EnumerateArray().Sum(group => group.GetProperty("count").GetInt64()) > root.GetProperty("total").GetInt64());
                break;
            case "catalog":
                ExactProperties(root, "generatedUtc", "total", "byStatus", "byCategoryAndStatus", "byKindAndStatus", "scope");
                Assert.Equal(8, root.GetProperty("total").GetInt64());
                CountGroups(root.GetProperty("byStatus"), ["draft", "published", "archived"], [2, 5, 1]);
                var categories = root.GetProperty("byCategoryAndStatus");
                Assert.Equal(18, categories.GetArrayLength());
                var categoryIndex = 0;
                foreach (var category in CatalogRules.Categories)
                    foreach (var status in new[] { "draft", "published", "archived" })
                    {
                        var group = categories[categoryIndex++];
                        ExactProperties(group, "category", "status", "count");
                        Assert.Equal(category.Id, group.GetProperty("category").GetString());
                        Assert.Equal(status, group.GetProperty("status").GetString());
                        Assert.Equal(CategoryCount(category.Id, status), group.GetProperty("count").GetInt64());
                    }
                Assert.Equal(root.GetProperty("total").GetInt64(), categories.EnumerateArray().Sum(group => group.GetProperty("count").GetInt64()));
                var kinds = root.GetProperty("byKindAndStatus");
                Assert.Equal(9, kinds.GetArrayLength());
                Assert.Equal(root.GetProperty("total").GetInt64(), kinds.EnumerateArray().Sum(group => group.GetProperty("count").GetInt64()));
                var kindIndex = 0;
                foreach (var kind in new[] { "work", "course", "program" })
                    foreach (var status in new[] { "draft", "published", "archived" })
                    {
                        var group = kinds[kindIndex++];
                        ExactProperties(group, "kind", "status", "count");
                        Assert.Equal(kind, group.GetProperty("kind").GetString());
                        Assert.Equal(status, group.GetProperty("status").GetString());
                        Assert.Equal(KindCount(kind, status), group.GetProperty("count").GetInt64());
                    }
                break;
            case "subscriptions":
                ExactProperties(root, "generatedUtc", "total", "byStatus", "scope");
                Assert.Equal(4, root.GetProperty("total").GetInt64());
                CountGroups(root.GetProperty("byStatus"), ["active", "cancelled", "suspended"], [2, 1, 1]);
                break;
            default: throw new InvalidOperationException("Unknown synthetic report module.");
        }
        Assert.Equal(root.GetProperty("total").GetInt64(), root.GetProperty("byStatus").EnumerateArray().Sum(group => group.GetProperty("count").GetInt64()));
        factory.Reports.AssertOnlyCalled(module);
    }

    [Theory]
    [MemberData(nameof(MatchingPermissions))]
    public async Task DatabaseFailureReturnsSanitizedUnavailableReportAndPreservesPrivateHeaders(string module)
    {
        await using var factory = new ReportFactory(module);
        factory.Reports.Fail = true;
        using var client = Client(factory);
        using var response = await client.GetAsync(Path(module), Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Contains("frame-ancestors 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        var body = await response.Content.ReadAsStringAsync(Token);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("service_unavailable", problem.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("qa-only-private-connection-detail", body, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-reports-connection", body, StringComparison.Ordinal);
        factory.Reports.AssertOnlyCalled(module);
    }

    private static string Path(string module) => "/api/v1/admin/reports/" + module;
    private static string Permission(string module) => module switch
    {
        "identity" => IdentityRules.ManageUsers,
        "catalog" => IdentityRules.ManageContent,
        "subscriptions" => IdentityRules.ManageSubscriptions,
        _ => throw new InvalidOperationException("Unknown synthetic report module.")
    };
    private static HttpClient Client(ReportFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    });
    private static void PrivateHeaders(HttpResponseMessage response)
    {
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }
    private static void ExactProperties(JsonElement value, params string[] properties) =>
        Assert.Equal(properties.OrderBy(name => name, StringComparer.Ordinal), value.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
    private static void CountGroups(JsonElement groups, string[] keys, long[] counts)
    {
        Assert.Equal(keys.Length, groups.GetArrayLength());
        Assert.Equal(keys.Length, counts.Length);
        for (var index = 0; index < keys.Length; index++)
        {
            var group = groups[index];
            ExactProperties(group, "key", "count");
            Assert.Equal(keys[index], group.GetProperty("key").GetString());
            Assert.Equal(counts[index], group.GetProperty("count").GetInt64());
        }
    }
    private static long CategoryCount(string category, string status) => (category, status) switch
    {
        ("lecturas", "draft") => 1,
        ("lecturas", "published") => 2,
        ("videos", "draft") => 1,
        ("videos", "published") => 1,
        ("podcast", "archived") => 1,
        ("cursos", "published") => 2,
        _ => 0
    };
    private static long KindCount(string kind, string status) => (kind, status) switch
    {
        ("work", "draft") => 2,
        ("work", "published") => 3,
        ("work", "archived") => 1,
        ("course", "published") => 1,
        ("program", "published") => 1,
        _ => 0
    };

    private sealed class ReportFactory(string module, bool authenticated = true, bool mfa = true, string? permission = null) : WebApplicationFactory<Program>
    {
        public ReportSpy Reports { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = "Host=127.0.0.1;Port=1;Database=acropolis_test_report_http;Username=synthetic_report_http;Timeout=1;Command Timeout=1",
                ["Identity:EmailEnabled"] = "false",
                ["Identity:PublicOrigin"] = "https://localhost"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IIdentityReportService>();
                services.RemoveAll<ICatalogReportService>();
                services.RemoveAll<ISubscriptionReportService>();
                services.AddSingleton<IIdentityReportService>(Reports);
                services.AddSingleton<ICatalogReportService>(Reports);
                services.AddSingleton<ISubscriptionReportService>(Reports);
                services.AddSingleton(new SyntheticSubject(authenticated, mfa, permission ?? Permission(module)));
                services.AddAuthentication(options =>
                {
                    options.DefaultScheme = ReportAuthenticationHandler.SchemeName;
                    options.DefaultAuthenticateScheme = ReportAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = ReportAuthenticationHandler.SchemeName;
                    options.DefaultForbidScheme = ReportAuthenticationHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, ReportAuthenticationHandler>(ReportAuthenticationHandler.SchemeName, _ => { });
            });
        }
    }

    private sealed record SyntheticSubject(bool Authenticated, bool Mfa, string Permission);
    private sealed class ReportAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "qa-operational-reports";
        private readonly SyntheticSubject subject;
        public ReportAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, SyntheticSubject subject)
            : base(options, logger, encoder) => this.subject = subject;
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

    private sealed class ReportSpy : IIdentityReportService, ICatalogReportService, ISubscriptionReportService
    {
        private int identityCalls;
        private int catalogCalls;
        private int subscriptionCalls;
        public bool Fail { get; set; }
        Task<IdentityReportView> IIdentityReportService.GetCurrentAsync(CancellationToken token)
        {
            Interlocked.Increment(ref identityCalls);
            if (Fail) throw new Npgsql.NpgsqlException("qa-only-private-connection-detail: synthetic-reports-connection");
            return Task.FromResult(new IdentityReportView(SnapshotTime, 5,
                [new("active", 3), new("pending", 1), new("disabled", 1)],
                [new("Externo", 2), new("Probacionista", 1), new("Miembro", 3), new("FFVV", 0), new("Instructor", 1), new("Hachado", 0)]));
        }
        Task<CatalogReportView> ICatalogReportService.GetCurrentAsync(CancellationToken token)
        {
            Interlocked.Increment(ref catalogCalls);
            if (Fail) throw new Npgsql.NpgsqlException("qa-only-private-connection-detail: synthetic-reports-connection");
            var states = new[] { "draft", "published", "archived" };
            return Task.FromResult(new CatalogReportView(SnapshotTime, 8,
                [new("draft", 2), new("published", 5), new("archived", 1)],
                CatalogRules.Categories.SelectMany(category => states.Select(status => new CatalogCategoryStateCount(category.Id, status, CategoryCount(category.Id, status)))).ToArray(),
                new[] { "work", "course", "program" }.SelectMany(kind => states.Select(status => new CatalogKindStateCount(kind, status, KindCount(kind, status)))).ToArray()));
        }
        Task<SubscriptionReportView> ISubscriptionReportService.GetCurrentAsync(CancellationToken token)
        {
            Interlocked.Increment(ref subscriptionCalls);
            if (Fail) throw new Npgsql.NpgsqlException("qa-only-private-connection-detail: synthetic-reports-connection");
            return Task.FromResult(new SubscriptionReportView(SnapshotTime, 4,
                [new("active", 2), new("cancelled", 1), new("suspended", 1)]));
        }
        public void AssertNoCalls()
        {
            Assert.Equal(0, Volatile.Read(ref identityCalls));
            Assert.Equal(0, Volatile.Read(ref catalogCalls));
            Assert.Equal(0, Volatile.Read(ref subscriptionCalls));
        }
        public void AssertOnlyCalled(string module)
        {
            Assert.Equal(module == "identity" ? 1 : 0, Volatile.Read(ref identityCalls));
            Assert.Equal(module == "catalog" ? 1 : 0, Volatile.Read(ref catalogCalls));
            Assert.Equal(module == "subscriptions" ? 1 : 0, Volatile.Read(ref subscriptionCalls));
        }
    }
}
