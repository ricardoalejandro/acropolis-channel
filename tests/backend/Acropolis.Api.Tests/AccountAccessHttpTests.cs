using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Acropolis.Identity.Application;
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

// Real host policies/private HTTP boundary; synthetic subject and service only.
// AccountAccessTests separately proves the PostgreSQL transaction and timestamps.
public sealed class AccountAccessHttpTests
{
    private static readonly Guid UserId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Recorded = new(2026, 10, 1, 12, 30, 0, TimeSpan.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Path => "/api/v1/admin/users/" + UserId + "/access";

    [Fact]
    public async Task AnonymousRequestsCannotReadAccessDatesOrInvokeTheService()
    {
        await using var factory = new AccessFactory(authenticated: false);
        using var client = Client(factory); using var response = await client.GetAsync(Path, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); PrivateHeaders(response); Assert.Equal(0, factory.Access.Calls);
    }
    [Theory]
    [InlineData(false, "Users.Manage", false)]
    [InlineData(true, "", false)]
    [InlineData(true, "Content.Manage", false)]
    [InlineData(true, "Subscriptions.Manage", false)]
    [InlineData(true, "", true)]
    public async Task MfaAndTheExactUserPermissionAreRequiredWithoutAnOwnerBypass(bool mfa, string permission, bool owner)
    {
        await using var factory = new AccessFactory(mfa: mfa, permission: permission, owner: owner);
        using var client = Client(factory); using var response = await client.GetAsync(Path, Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); PrivateHeaders(response); Assert.Equal(0, factory.Access.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorizedResponsesContainOnlyTheNullableUtcDateAndNeverSessionOrIdentityDetails(bool recorded)
    {
        await using var factory = new AccessFactory(); factory.Access.Value = new(recorded ? Recorded : null);
        using var client = Client(factory); using var response = await client.GetAsync(Path, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); PrivateHeaders(response);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(["lastSignInUtc"], body.EnumerateObject().Select(x => x.Name).ToArray());
        if (recorded) Assert.Equal(Recorded, body.GetProperty("lastSignInUtc").GetDateTimeOffset());
        else Assert.Equal(JsonValueKind.Null, body.GetProperty("lastSignInUtc").ValueKind);
        Assert.Equal(1, factory.Access.Calls); Assert.Equal(UserId, factory.Access.UserId);
        Assert.DoesNotContain("auth_version", body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("ticket", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task MissingUsersReturn404RatherThanAnInventedUnknownAccount()
    {
        await using var factory = new AccessFactory(); factory.Access.Value = null;
        using var client = Client(factory); using var response = await client.GetAsync(Path, Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); PrivateHeaders(response);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal("not_found", body.GetProperty("code").GetString()); Assert.Equal(1, factory.Access.Calls);
    }
    [Fact]
    public async Task StorageFailuresAreSanitized503WithPrivateAndSecurityHeaders()
    {
        await using var factory = new AccessFactory(); factory.Access.Fail = true;
        using var client = Client(factory); using var response = await client.GetAsync(Path, Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); PrivateHeaders(response);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("synthetic-private-access-connection", body, StringComparison.Ordinal);
        Assert.Equal("service_unavailable", JsonSerializer.Deserialize<JsonElement>(body).GetProperty("code").GetString());
        Assert.Equal(1, factory.Access.Calls);
    }
    private static HttpClient Client(AccessFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    private static void PrivateHeaders(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
    }
    private sealed class AccessFactory(bool authenticated = true, bool mfa = true, string permission = "Users.Manage", bool owner = false) : WebApplicationFactory<Program>
    {
        public AccessSpy Access { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = "Host=127.0.0.1;Port=1;Database=acropolis_test_access_http;Username=synthetic_access_http;Timeout=1;Command Timeout=1",
                ["Identity:EmailEnabled"] = "false",
                ["Identity:PublicOrigin"] = "https://localhost"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAccountAccessService>(); services.AddSingleton<IAccountAccessService>(Access);
                services.AddSingleton(new Subject(authenticated, mfa, permission, owner));
                services.AddAuthentication(options =>
                {
                    options.DefaultScheme = AccessHandler.SchemeName; options.DefaultAuthenticateScheme = AccessHandler.SchemeName;
                    options.DefaultChallengeScheme = AccessHandler.SchemeName; options.DefaultForbidScheme = AccessHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, AccessHandler>(AccessHandler.SchemeName, _ => { });
            });
        }
    }
    private sealed record Subject(bool Authenticated, bool Mfa, string Permission, bool Owner);
    private sealed class AccessHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "qa-account-access"; private readonly Subject subject;
        public AccessHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, Subject subject)
            : base(options, logger, encoder) => this.subject = subject;
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!subject.Authenticated) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, UserId.ToString()), new("amr", subject.Mfa ? "mfa" : "pwd") };
            if (subject.Permission.Length > 0) claims.Add(new("permission", subject.Permission));
            if (subject.Owner) claims.Add(new("is_owner", "true"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
        protected override Task HandleChallengeAsync(AuthenticationProperties properties) => IdentityEndpoints.Problem("invalid_credentials", 401).ExecuteAsync(Context);
        protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => IdentityEndpoints.Problem("forbidden", 403).ExecuteAsync(Context);
    }
    private sealed class AccessSpy : IAccountAccessService
    {
        public int Calls { get; private set; }
        public Guid UserId { get; private set; }
        public bool Fail { get; set; }
        public UserAccessView? Value { get; set; } = new(Recorded);
        public Task<UserAccessView?> GetAsync(Guid userId, CancellationToken token)
        {
            Calls++; UserId = userId;
            if (Fail) throw new Npgsql.NpgsqlException("synthetic-private-access-connection");
            return Task.FromResult(Value);
        }
    }
}
