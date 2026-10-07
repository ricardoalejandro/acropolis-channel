using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Acropolis.Catalog.Application;
using Acropolis.Identity.Application;
using Acropolis.Catalog.Infrastructure;
using Microsoft.Extensions.Hosting;
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

// A single host with fresh clients tests real middleware, policies and binding.
// Separate PostgreSQL tests prove recording transactions and aggregates.
public sealed class ConsumptionActivityHttpTests(ConsumptionActivityHttpFixture fixture) : IClassFixture<ConsumptionActivityHttpFixture>
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private ConsumptionActivityHttpFixture Reset() { fixture.Reset(); return fixture; }
    private static string Query => "?from=2026-10-07&to=2026-10-08";
    private static string StartPath => "/api/v1/consumption/content/lectura-qa/sessions";
    private static StartConsumptionRequest Start => new(Guid.Parse("10000000-0000-0000-0000-000000000002"), new string('b', 32));
    public static TheoryData<string, bool, bool, string> DeniedReports => new()
    {
        { "general", false, true, IdentityRules.ManageContent },
        { "general", true, false, IdentityRules.ManageContent },
        { "general", true, true, IdentityRules.ManageUsers },
        { "content", false, true, IdentityRules.ManageContent },
        { "content", true, false, IdentityRules.ManageContent },
        { "content", true, true, IdentityRules.ManageUsers },
        { "account", false, true, IdentityRules.ManageContent + "," + IdentityRules.ManageUsers },
        { "account", true, false, IdentityRules.ManageContent + "," + IdentityRules.ManageUsers },
        { "account", true, true, IdentityRules.ManageContent },
        { "account", true, true, IdentityRules.ManageUsers }
    };
    [Theory, MemberData(nameof(DeniedReports))]
    public async Task ReportPoliciesPrecedeQueryAndRequireMfaAndBothAccountPermissions(string route, bool authenticated, bool mfa, string permissions)
    {
        var host = Reset(); host.Subject.Authenticated = authenticated; host.Subject.Mfa = mfa; host.Subject.Permissions = permissions.Split(',');
        using var client = host.Client(); using var response = await client.GetAsync(host.ReportPath(route) + "?unknown=private-do-not-echo", Token);
        Assert.Equal(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, response.StatusCode);
        Private(response); Assert.Equal(0, host.Spy.ReportCalls); Assert.Equal(0, host.Identity.LookupCalls);
        Assert.DoesNotContain("private-do-not-echo", await response.Content.ReadAsStringAsync(Token));
    }
    [Theory, InlineData("general"), InlineData("content"), InlineData("account")]
    public async Task PermittedReportsExposeOnlyRecordedMetricsAndExplicitAvailability(string route)
    {
        var host = Reset(); host.Subject.Permissions = [IdentityRules.ManageContent, IdentityRules.ManageUsers];
        using var client = host.Client(); using var response = await client.GetAsync(host.ReportPath(route) + Query, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Private(response);
        var raw = await response.Content.ReadAsStringAsync(Token); using var doc = JsonDocument.Parse(raw); var root = doc.RootElement;
        Assert.Equal(route == "account" ? "recorded_consumption_account" : "recorded_consumption_general", root.GetProperty("scope").GetString());
        Assert.False(root.GetProperty("recordingEnabled").GetBoolean());
        Assert.Equal(route == "account" ? 90 : 365, root.GetProperty("retentionDays").GetInt32());
        Assert.Equal(1, host.Spy.ReportCalls); Assert.Equal(route, host.Spy.LastReport);
        Assert.DoesNotContain("Synthetic private name", raw); Assert.DoesNotContain("private-account@example.test", raw); Assert.DoesNotContain(host.Subject.Id.ToString(), raw);
        foreach (var key in new[] { "accountId", "email", "workText", "youTubeId", "authenticationBindingHash" }) Assert.False(root.TryGetProperty(key, out _));
        if (route != "content") Assert.Equal(0, root.GetProperty("summary").GetProperty("starts").GetInt64());
        Assert.Equal(0, host.Spy.StartCalls + host.Spy.PulseCalls);
    }
    [Fact]
    public async Task StrictDatesDuplicatesUnknownFieldsAndPagingRejectBeforeServices()
    {
        var host = Reset(); using var client = host.Client();
        foreach (var query in new[] { "", "?from=2026-10-07", "?from=2026-10-07&from=2026-10-07&to=2026-10-08", "?from=2026-10-07&to=2026-10-07", "?from=2025-10-01&to=2026-10-03", "?from=2026-02-29&to=2026-03-01", "?from=2026-10-07%0A&to=2026-10-08", Query + "&From=2026-10-07", Query + "&unknown=secret" })
        {
            using var response = await client.GetAsync(host.ReportPath("general") + query, Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Private(response); await Code(response, "validation_error");
        }
        foreach (var query in new[] { Query + "&page=0", Query + "&pageSize=101", Query + "&page=1%0A", Query + "&page=1&page=1", Query + "&pageSize=01" })
        {
            using var response = await client.GetAsync(host.ReportPath("content") + query, Token); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Private(response);
        }
        Assert.Equal(0, host.Spy.ReportCalls);
    }
    [Fact]
    public async Task MissingAccountIsNotFoundAndUnavailableReportsRemainSanitized()
    {
        var host = Reset(); host.Subject.Permissions = [IdentityRules.ManageContent, IdentityRules.ManageUsers]; host.Identity.Exists = false;
        using var client = host.Client(); using var missing = await client.GetAsync(host.ReportPath("account") + Query, Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode); Private(missing); Assert.Equal(0, host.Spy.ReportCalls);
        host.Identity.Exists = true; host.Spy.Fail = true;
        using var failed = await client.GetAsync(host.ReportPath("general") + Query, Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode); Private(failed); await Code(failed, "service_unavailable");
        var raw = await failed.Content.ReadAsStringAsync(Token); Assert.DoesNotContain("private-database-password", raw); Assert.DoesNotContain("host-qa-private", raw);
    }
    [Fact]
    public async Task CapabilitiesDoNotStartRecordingAndRejectQueries()
    {
        var host = Reset(); using var client = host.Client();
        using var response = await client.GetAsync("/api/v1/consumption/capabilities", Token); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Private(response);
        var capabilities = (await response.Content.ReadFromJsonAsync<ConsumptionCapabilitiesView>(Token))!;
        Assert.False(capabilities.RecordingEnabled); Assert.Equal(90, capabilities.DetailRetentionDays); Assert.Equal(365, capabilities.GeneralRetentionDays);
        Assert.True(Assert.Single(host.Services.GetServices<IHostedService>().OfType<ConsumptionRetentionWorker>()).ExecuteTask?.IsCompletedSuccessfully);
        Assert.Equal(0, host.Spy.StartCalls + host.Spy.PulseCalls);
        using var invalid = await client.GetAsync("/api/v1/consumption/capabilities?unknown=1", Token); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Private(invalid);
    }

    [Theory, InlineData("capabilities"), InlineData("start"), InlineData("pulse")]
    public async Task AnonymousConsumptionCannotReachCapabilitiesOrWritesBeforeBodyAndCsrf(string operation)
    {
        var host = Reset(); host.Subject.Authenticated = false; using var client = host.Client();
        var path = operation == "capabilities" ? "/api/v1/consumption/capabilities" : operation == "start" ? StartPath : "/api/v1/consumption/sessions/" + host.Spy.Session + "/pulses";
        using var response = operation == "capabilities" ? await client.GetAsync(path, Token) : await client.PostAsync(path, new StringContent(new string('x', 9000), Encoding.UTF8, "application/json"), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Private(response); Assert.Equal(0, host.Spy.StartCalls + host.Spy.PulseCalls);
    }
    [Theory, InlineData("unconfirmed", 401), InlineData("disabled", 401), InlineData("missing", 401), InlineData("binding", 401), InlineData("subscription", 403)]
    public async Task EachRecordingWriteRechecksLiveEligibility(string failure, int expected)
    {
        var host = Reset();
        if (failure == "unconfirmed") host.Identity.Confirmed = false;
        if (failure == "disabled") host.Identity.Active = false;
        if (failure == "missing") host.Identity.Exists = false;
        if (failure == "binding") host.Subject.SecurityVersion = "invalid";
        if (failure == "subscription") host.Access.Enabled = false;
        using var client = host.Client(); using var response = await Write(client, StartPath, JsonContent.Create(Start));
        Assert.Equal((HttpStatusCode)expected, response.StatusCode); Private(response); Assert.Equal(0, host.Spy.StartCalls + host.Spy.PulseCalls);
    }
    [Fact]
    public async Task RecordingUsesServerAccountAndHashedAuthenticationBindingAndExplicitPulse()
    {
        var host = Reset(); using var client = host.Client();
        using var started = await Write(client, StartPath, JsonContent.Create(Start)); Assert.Equal(HttpStatusCode.OK, started.StatusCode); Private(started);
        Assert.Equal(host.Subject.Id, host.Spy.Account); Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(host.Subject.SecurityVersion))), host.Spy.Binding);
        Assert.Equal(1, host.Spy.StartCalls); Assert.Equal(0, host.Spy.PulseCalls); Assert.Equal(Start, host.Spy.Start);
        var pulse = new ConsumptionPulseRequest(1, 1000, 1000, Reading: new(5000, [new(0, 5000)]));
        using var recorded = await Write(client, "/api/v1/consumption/sessions/" + host.Spy.Session + "/pulses", JsonContent.Create(pulse));
        Assert.Equal(HttpStatusCode.OK, recorded.StatusCode); Private(recorded); Assert.Equal(1, host.Spy.PulseCalls); Assert.Equal(pulse.Sequence, host.Spy.Pulse?.Sequence);
        host.Access.Enabled = false;
        using var removedAccess = await Write(client, "/api/v1/consumption/sessions/" + host.Spy.Session + "/pulses", JsonContent.Create(pulse));
        Assert.Equal(HttpStatusCode.Forbidden, removedAccess.StatusCode); Assert.Equal(1, host.Spy.PulseCalls);
    }
    [Fact]
    public async Task CsrfAndUnknownJsonFieldsCannotInvokeRecording()
    {
        var host = Reset(); using var client = host.Client();
        using var noCsrf = await client.PostAsJsonAsync(StartPath, Start, Token); Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode); Private(noCsrf); await Code(noCsrf, "csrf_invalid");
        using var unknown = await Write(client, StartPath, new StringContent(JsonSerializer.Serialize(Start, JsonSerializerOptions.Web).TrimEnd('}') + ",\"accountId\":\"private-do-not-echo\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode); Private(unknown); Assert.Equal(0, host.Spy.StartCalls);
        Assert.DoesNotContain("private-do-not-echo", await unknown.Content.ReadAsStringAsync(Token));
    }
    [Theory, InlineData(false), InlineData(true)]
    public async Task KnownLengthAndChunkedBodiesEnforceEightKiBExactly(bool chunked)
    {
        var host = Reset(); using var client = host.Client(); var json = JsonSerializer.Serialize(Start, JsonSerializerOptions.Web);
        HttpContent Body(int bytes) => chunked ? new UnknownLengthContent(json + new string(' ', bytes - Encoding.UTF8.GetByteCount(json))) : new StringContent(json + new string(' ', bytes - Encoding.UTF8.GetByteCount(json)), Encoding.UTF8, "application/json");
        using var tooLarge = await Write(client, StartPath, Body(8193)); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode); Private(tooLarge); await Code(tooLarge, "validation_error"); Assert.Equal(0, host.Spy.StartCalls);
        using var exact = await Write(client, StartPath, Body(8192)); Assert.Equal(HttpStatusCode.OK, exact.StatusCode); Assert.Equal(1, host.Spy.StartCalls);
    }
    [Fact]
    public async Task RecordingRateLimitRejectsDuplicateFloodWithoutInvokingAdditionalWrites()
    {
        var host = Reset(); using var client = host.Client();
        for (var number = 0; number < 30; number++) { using var response = await Write(client, StartPath, JsonContent.Create(Start)); Assert.Equal(HttpStatusCode.OK, response.StatusCode); }
        using var rejected = await Write(client, StartPath, JsonContent.Create(Start)); Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode); Private(rejected); Assert.Equal(30, host.Spy.StartCalls);
    }
    private async Task<HttpResponseMessage> Write(HttpClient client, string path, HttpContent body)
    {
        using var csrf = await client.GetAsync("/api/v1/identity/csrf", Token); var token = (await csrf.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("token").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body }; request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request, Token);
    }
    private async Task Code(HttpResponseMessage response, string expected) { using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)); Assert.Equal(expected, doc.RootElement.GetProperty("code").GetString()); }
    private static void Private(HttpResponseMessage response)
    {
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString()); Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options"))); Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
    }
    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] bytes;
        public UnknownLengthContent(string body)
        {
            bytes = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await stream.WriteAsync(bytes);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => await stream.WriteAsync(bytes, cancellationToken);
    }
}

public sealed class ConsumptionActivityHttpFixture : WebApplicationFactory<Program>
{
    public SubjectState Subject { get; } = new();
    public IdentitySpy Identity { get; } = new();
    public AccessSpy Access { get; } = new();
    public RecordingReportSpy Spy { get; } = new();
    public void Reset() { Subject.Reset(); Identity.Reset(); Access.Enabled = true; Spy.Reset(); }
    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    public string ReportPath(string route) => route switch { "general" => "/api/v1/admin/reports/catalog/consumption", "content" => "/api/v1/admin/reports/catalog/consumption/content", "account" => "/api/v1/admin/users/" + Subject.Id + "/consumption", _ => throw new ArgumentException("Unknown QA route.") };
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = "Host=127.0.0.1;Port=1;Database=acropolis_test_consumption_http;Username=qa_http;Timeout=1;Command Timeout=1", ["Identity:EmailEnabled"] = "false", ["Identity:PublicOrigin"] = "https://localhost" }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IIdentityService>(); services.AddSingleton<IIdentityService>(Identity);
            services.RemoveAll<ISubscriptionAccess>(); services.AddSingleton<ISubscriptionAccess>(Access);
            services.RemoveAll<IConsumptionRecordingService>(); services.AddSingleton<IConsumptionRecordingService>(Spy);
            services.RemoveAll<IConsumptionActivityReportService>(); services.AddSingleton<IConsumptionActivityReportService>(Spy);
            services.AddSingleton(Subject);
            services.AddAuthentication(options => { options.DefaultScheme = Authentication.SchemeName; options.DefaultAuthenticateScheme = Authentication.SchemeName; options.DefaultChallengeScheme = Authentication.SchemeName; options.DefaultForbidScheme = Authentication.SchemeName; }).AddScheme<AuthenticationSchemeOptions, Authentication>(Authentication.SchemeName, _ => { });
        });
    }
    public sealed class SubjectState
    {
        public Guid Id { get; set; }
        public bool Authenticated { get; set; }
        public bool Mfa { get; set; }
        public string[] Permissions { get; set; } = [];
        public string SecurityVersion { get; set; } = "";
        public void Reset() { Id = Guid.NewGuid(); Authenticated = true; Mfa = true; Permissions = [IdentityRules.ManageContent]; SecurityVersion = new string('a', 32); }
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, SubjectState subject) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "qa-consumption-activity";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!subject.Authenticated) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject.Id.ToString()), new("amr", subject.Mfa ? "mfa" : "pwd"), new("auth_version", subject.SecurityVersion) };
            claims.AddRange(subject.Permissions.Select(value => new Claim("permission", value)));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
        protected override Task HandleChallengeAsync(AuthenticationProperties properties) => IdentityEndpoints.Problem("invalid_credentials", 401).ExecuteAsync(Context);
        protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => IdentityEndpoints.Problem("forbidden", 403).ExecuteAsync(Context);
    }
    public sealed class AccessSpy : ISubscriptionAccess { public bool Enabled { get; set; } public Task<bool> HasActiveAsync(Guid id, CancellationToken token) => Task.FromResult(Enabled); }
    public sealed class IdentitySpy : IIdentityService
    {
        public bool Exists { get; set; } = true; public bool Confirmed { get; set; } = true; public bool Active { get; set; } = true; public int LookupCalls { get; set; }
        public void Reset() { Exists = Confirmed = Active = true; LookupCalls = 0; }
        public Task<UserView?> GetUserAsync(Guid id, CancellationToken token) { LookupCalls++; return Task.FromResult(Exists ? new UserView(id, "Synthetic private name", "private-account@example.test", Confirmed, Active ? "active" : "disabled", [], [], new string('c', 32)) : null); }
        public Task<IdentityResult<bool>> RegisterAsync(RegisterRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<IdentityResult<AuthenticatedUser>> LoginAsync(LoginRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task RequestEmailAsync(string email, string purpose, CancellationToken token) => throw new NotSupportedException();
        public Task<IdentityResult<bool>> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<IdentityResult<bool>> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<IdentityResult<bool>> ChangePasswordAsync(Guid id, ChangePasswordRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<IdentityAccountSummary[]> LookupAccountsAsync(IReadOnlyCollection<Guid> ids, CancellationToken token) => throw new NotSupportedException();
        public Task<IdentityResult<UserView>> UpdateProfileAsync(Guid id, ProfileRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<UserPage> ListUsersAsync(string? search, string? status, string? level, int page, int pageSize, CancellationToken token) => throw new NotSupportedException();
        public Task<IdentityResult<UserView>> UpdateUserAsync(Guid actor, Guid id, AdminUserRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<UserAuditPage> ListAuditAsync(DateTimeOffset? from, DateTimeOffset? to, string? action, Guid? id, int page, int pageSize, CancellationToken token) => throw new NotSupportedException();
        public Task<IdentityResult<UserView>> UpdatePermissionsAsync(Guid actor, Guid id, AdminPermissionsRequest request, CancellationToken token) => throw new NotSupportedException();
    }
    public sealed class RecordingReportSpy : IConsumptionRecordingService, IConsumptionActivityReportService
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        private static ConsumptionMetrics Zero => new(0, 0, 0, 0, 0, 0, 0, 0);
        public Guid Session { get; } = Guid.NewGuid(); public int StartCalls { get; set; }
        public int PulseCalls { get; set; }
        public int ReportCalls { get; set; }
        public string? LastReport { get; set; }
        public Guid Account { get; set; }
        public string? Binding { get; set; }
        public StartConsumptionRequest? Start { get; set; }
        public ConsumptionPulseRequest? Pulse { get; set; }
        public bool Fail { get; set; }
        public void Reset() { StartCalls = PulseCalls = ReportCalls = 0; LastReport = Binding = null; Start = null; Pulse = null; Fail = false; }
        public ConsumptionCapabilitiesView GetCapabilities() => new(false, 90, 365);
        public Task<CatalogResult<ConsumptionSessionView>> StartAsync(Guid id, string binding, string slug, StartConsumptionRequest request, CancellationToken token) { StartCalls++; Account = id; Binding = binding; Start = request; return Task.FromResult(new CatalogResult<ConsumptionSessionView>(new(Session, Now, 1, "reading"))); }
        public Task<CatalogResult<ConsumptionPulseReceipt>> PulseAsync(Guid id, string binding, Guid session, ConsumptionPulseRequest request, CancellationToken token) { PulseCalls++; Pulse = request; return Task.FromResult(new CatalogResult<ConsumptionPulseReceipt>(new(request.Sequence, Now, 1000, 5000, false, false))); }
        private void Called(string name) { ReportCalls++; LastReport = name; if (Fail) throw new Npgsql.NpgsqlException("private-database-password at host-qa-private"); }
        public Task<ConsumptionReportView> GetAsync(ConsumptionReportInterval interval, CancellationToken token) { Called("general"); return Task.FromResult(new ConsumptionReportView(Now, interval.FromUtc, interval.ToUtc, ConsumptionActivityRules.GeneralAvailableFrom(Now), ConsumptionActivityRules.DetailAvailableFrom(Now), 365, false, Zero, [new("reading", Zero), new("youtube", Zero)], CatalogRules.Categories.Select(x => new ConsumptionGroup(x.Id, Zero)).ToArray(), [new(interval.From, Zero)])); }
        public Task<ConsumptionContentPage> ListContentAsync(ConsumptionReportInterval interval, int page, int pageSize, CancellationToken token) { Called("content"); return Task.FromResult(new ConsumptionContentPage(Now, interval.FromUtc, interval.ToUtc, ConsumptionActivityRules.GeneralAvailableFrom(Now), ConsumptionActivityRules.DetailAvailableFrom(Now), 365, false, [], 0, page, pageSize)); }
        public Task<AccountConsumptionReportView> GetAccountAsync(Guid id, ConsumptionReportInterval interval, int page, int pageSize, CancellationToken token) { Called("account"); return Task.FromResult(new AccountConsumptionReportView(Now, interval.FromUtc, interval.ToUtc, ConsumptionActivityRules.DetailAvailableFrom(Now), 90, false, Zero, [new(interval.From, Zero)], [], 0, page, pageSize)); }
    }
}
