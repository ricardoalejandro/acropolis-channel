using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Acropolis.Catalog.Application;
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

public sealed class TopicHttpTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Base = "/api/v1/admin/catalog/topics";
    private static readonly Guid TopicId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid ContentId = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly string Version = new('a', 32);
    [Theory]
    [InlineData(false, true, "Content.Manage", 401)]
    [InlineData(true, false, "Content.Manage", 403)]
    [InlineData(true, true, "Users.Manage", 403)]
    [InlineData(true, true, "Subscriptions.Manage", 403)]
    [InlineData(true, true, "", 403)]
    public async Task EveryAdministrativeRouteRequiresContentPermissionAndMfaBeforeCallingService(bool authenticated, bool mfa, string permission, int status)
    {
        await using var factory = new TopicFactory(authenticated, mfa, permission); using var client = Client(factory);
        foreach (var path in new[] { Base, Base + "/" + TopicId, Base + "/" + TopicId + "/audit", "/api/v1/admin/content/" + ContentId + "/topics" })
        {
            using var response = await client.GetAsync(path, Token); Assert.Equal(status, (int)response.StatusCode); Headers(response);
        }
        foreach (var path in new[] { Base, Base + "/" + TopicId, Base + "/" + TopicId + "/state", Base + "/order", "/api/v1/admin/content/" + ContentId + "/topics" })
        {
            using var message = new HttpRequestMessage(path == Base ? HttpMethod.Post : HttpMethod.Put, path) { Content = JsonContent.Create(new { name = "Ignored without authorization" }) };
            using var response = await client.SendAsync(message, Token); Assert.Equal(status, (int)response.StatusCode); Headers(response);
        }
        Assert.Equal(0, factory.Topics.Calls);
    }
    [Fact]
    public async Task PublicDirectoryAndFilterExposeOnlyMetadataAndArchivedOrUnknownFilterIsAnEmptySuccess()
    {
        await using var factory = new TopicFactory(false); using var client = Client(factory);
        using var response = await client.GetAsync("/api/v1/catalog/topics", Token); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Headers(response);
        var root = await response.Content.ReadFromJsonAsync<JsonElement>(Token); var topic = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "id", "name", "position", "slug" }, topic.EnumerateObject().Select(x => x.Name).Order());
        using var content = await client.GetAsync("/api/v1/catalog/content?topic=tema-qa&category=lecturas&search=50%25_", Token);
        Assert.Equal(HttpStatusCode.OK, content.StatusCode); Headers(content); Assert.Equal("tema-qa", factory.Topics.LastTopic); Assert.Equal("50%_", factory.Topics.LastSearch); Assert.Equal("lecturas", factory.Topics.LastCategory);
        foreach (var slug in new[] { "unknown-qa", "archived-qa" }) Assert.Empty((await client.GetFromJsonAsync<ContentPage>("/api/v1/catalog/content?topic=" + slug, Token))!.Items);
        var text = await content.Content.ReadAsStringAsync(Token); foreach (var forbidden in new[] { "workText", "youTubeId", "actorId", "version", "email" }) Assert.DoesNotContain(forbidden, text);
    }
    [Theory]
    [InlineData("topic=Invalid", "/api/v1/catalog/content")]
    [InlineData("topic=tema%0A", "/api/v1/catalog/content")]
    [InlineData("page=0", "/api/v1/catalog/topics")]
    [InlineData("pageSize=101", "/api/v1/catalog/topics")]
    [InlineData("status=published", Base)]
    [InlineData("search=tema%00", Base)]
    public async Task InvalidQueriesAreRejectedWithoutCallingTopicService(string query, string path)
    {
        await using var factory = new TopicFactory(); using var client = Client(factory);
        using var response = await client.GetAsync(path + "?" + query, Token); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Headers(response);
        Assert.Equal("validation_error", (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString()); Assert.Equal(0, factory.Topics.Calls);
    }
    [Fact]
    public async Task WritesRequireRealCsrfOriginAndAcceptOnlyTheNarrowContracts()
    {
        await using var factory = new TopicFactory(); using var client = Client(factory);
        using var without = await client.PostAsJsonAsync(Base, new CreateTopicRequest("tema-qa", "Tema QA"), Token); Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode); Assert.Equal(0, factory.Topics.Calls);
        using var extra = await Write(client, HttpMethod.Post, Base, new { slug = "tema-qa", name = "Tema QA", status = "archived" }); Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode); Assert.Equal(0, factory.Topics.Calls);
        using var foreign = await Write(client, HttpMethod.Post, Base, new CreateTopicRequest("tema-qa", "Tema QA"), "https://foreign.example"); Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode); Assert.Equal(0, factory.Topics.Calls);
        using var created = await Write(client, HttpMethod.Post, Base, new CreateTopicRequest("tema-qa", "Tema QA")); Assert.Equal(HttpStatusCode.Created, created.StatusCode); Assert.Equal(Base + "/" + TopicId, created.Headers.Location!.OriginalString); Headers(created);
        using var rename = await Write(client, HttpMethod.Put, Base + "/" + TopicId, new UpdateTopicRequest(Version, "Nuevo nombre QA")); Assert.Equal(HttpStatusCode.OK, rename.StatusCode); Assert.Equal("Nuevo nombre QA", factory.Topics.LastName);
        using var archive = await Write(client, HttpMethod.Put, Base + "/" + TopicId + "/state", new TopicStateRequest(Version, "archived")); Assert.Equal(HttpStatusCode.OK, archive.StatusCode); Assert.Equal("archived", factory.Topics.LastState);
        using var order = await Write(client, HttpMethod.Put, Base + "/order", new MoveTopicRequest(Version, TopicId, null)); Assert.Equal(HttpStatusCode.OK, order.StatusCode); Assert.Null(factory.Topics.LastBefore);
        using var assigned = await Write(client, HttpMethod.Put, "/api/v1/admin/content/" + ContentId + "/topics", new AssignContentTopicsRequest(Version, [TopicId])); Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        Assert.Equal(new[] { TopicId }, factory.Topics.LastIds); Assert.Equal(5, factory.Topics.Calls);
    }
    [Fact]
    public async Task ConflictsAndNotFoundArePassedWithoutPrivateExceptionDetails()
    {
        await using var factory = new TopicFactory(); using var client = Client(factory);
        using var missing = await client.GetAsync(Base + "/" + Guid.Empty, Token); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        factory.Topics.Conflict = true;
        using var response = await Write(client, HttpMethod.Put, Base + "/" + TopicId, new UpdateTopicRequest(Version, "No perder")); Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("concurrency_conflict", (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString()); Headers(response);
    }
    [Fact]
    public async Task UnavailableDatabaseIsSanitizedAndAnExplicitReadRetryIsPossible()
    {
        await using var factory = new TopicFactory(); using var client = Client(factory); factory.Topics.Fail = true;
        using var failed = await client.GetAsync(Base, Token); Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode); Headers(failed);
        var text = await failed.Content.ReadAsStringAsync(Token); Assert.DoesNotContain("synthetic-private-connection", text); Assert.Contains("service_unavailable", text);
        factory.Topics.Fail = false; using var retried = await client.GetAsync(Base, Token); Assert.Equal(HttpStatusCode.OK, retried.StatusCode); Assert.Equal(2, factory.Topics.Calls);
    }
    private static void Headers(HttpResponseMessage response) { Assert.True(response.Headers.CacheControl?.NoStore == true); Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy"))); }
    private static HttpClient Client(TopicFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    private static async Task<HttpResponseMessage> Write(HttpClient client, HttpMethod method, string path, object body, string origin = "https://localhost")
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf", Token);
        using var message = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        message.Headers.Add("Origin", origin); message.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()); return await client.SendAsync(message, Token);
    }
    private sealed record Subject(bool Authenticated, bool Mfa, string Permission);
    private sealed class TopicFactory(bool authenticated = true, bool mfa = true, string permission = "Content.Manage") : WebApplicationFactory<Program>
    {
        public TopicSpy Topics { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing"); builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = "Host=127.0.0.1;Port=1;Database=acropolis_test_topic_http;Username=synthetic_topic;Timeout=1;Command Timeout=1", ["Identity:EmailEnabled"] = "false", ["Identity:PublicOrigin"] = "https://localhost" }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITopicService>(); services.AddSingleton<ITopicService>(Topics); services.AddSingleton(new Subject(authenticated, mfa, permission));
                services.AddAuthentication(options => { options.DefaultScheme = "qa-topics"; options.DefaultAuthenticateScheme = "qa-topics"; options.DefaultChallengeScheme = "qa-topics"; options.DefaultForbidScheme = "qa-topics"; }).AddScheme<AuthenticationSchemeOptions, TopicAuth>("qa-topics", _ => { });
            });
        }
    }
    private sealed class TopicAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, Subject subject) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!subject.Authenticated) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "30000000-0000-0000-0000-000000000003"), new("amr", subject.Mfa ? "mfa" : "pwd") }; if (subject.Permission.Length > 0) claims.Add(new("permission", subject.Permission));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
        protected override Task HandleChallengeAsync(AuthenticationProperties properties) => IdentityEndpoints.Problem("invalid_credentials", 401).ExecuteAsync(Context);
        protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => IdentityEndpoints.Problem("forbidden", 403).ExecuteAsync(Context);
    }
    private sealed class TopicSpy : ITopicService
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public bool Conflict { get; set; }
        public string? LastTopic { get; private set; }
        public string? LastSearch { get; private set; }
        public string? LastCategory { get; private set; }
        public string? LastName { get; private set; }
        public string? LastState { get; private set; }
        public Guid? LastBefore { get; private set; }
        public Guid[] LastIds { get; private set; } = [];
        private static AdminTopicView Topic => new(TopicId, "tema-qa", "Tema QA", "active", 0, Version);
        private Task<T> Value<T>(T value, CancellationToken token) { Calls++; token.ThrowIfCancellationRequested(); if (Fail) throw new Npgsql.NpgsqlException("synthetic-private-connection"); return Task.FromResult(value); }
        public Task<PublicTopicPage> ListPublicAsync(int page, int pageSize, CancellationToken token) => Value(new PublicTopicPage([new(TopicId, "tema-qa", "Tema QA", 0)], 1, page, pageSize), token);
        public Task<AdminTopicPage> ListAdminAsync(string? search, string? status, int page, int pageSize, CancellationToken token) => Value(new AdminTopicPage([Topic], 1, page, pageSize, Version), token);
        public Task<AdminTopicView?> GetAdminAsync(Guid id, CancellationToken token) => Value<AdminTopicView?>(id == TopicId ? Topic : null, token);
        public Task<TopicAuditPage?> ListAuditAsync(Guid id, int page, int pageSize, CancellationToken token) => Value<TopicAuditPage?>(new([], 0, page, pageSize), token);
        public Task<ContentPage> ListPublishedByTopicAsync(string topic, string? search, string? category, int page, int pageSize, CancellationToken token) { LastTopic = topic; LastSearch = search; LastCategory = category; return Value(new ContentPage([], 0, page, pageSize), token); }
        public Task<ContentTopicsView?> GetContentTopicsAsync(Guid id, CancellationToken token) => Value<ContentTopicsView?>(new(id, Version, [Topic]), token);
        public Task<CatalogResult<AdminTopicView>> CreateAsync(Guid actorId, CreateTopicRequest request, CancellationToken token) => Value(new CatalogResult<AdminTopicView>(Topic), token);
        public Task<CatalogResult<AdminTopicView>> UpdateAsync(Guid actorId, Guid id, UpdateTopicRequest request, CancellationToken token) { LastName = request.Name; return Value(Conflict ? CatalogResult<AdminTopicView>.Fail("concurrency_conflict", 409) : new(Topic with { Name = request.Name }), token); }
        public Task<CatalogResult<AdminTopicView>> SetStateAsync(Guid actorId, Guid id, TopicStateRequest request, CancellationToken token) { LastState = request.Status; return Value(new CatalogResult<AdminTopicView>(Topic with { Status = request.Status }), token); }
        public Task<CatalogResult<AdminTopicView>> MoveAsync(Guid actorId, MoveTopicRequest request, CancellationToken token) { LastBefore = request.BeforeId; return Value(new CatalogResult<AdminTopicView>(Topic), token); }
        public Task<CatalogResult<ContentTopicsView>> AssignAsync(Guid actorId, Guid id, AssignContentTopicsRequest request, CancellationToken token) { LastIds = request.TopicIds; return Value(new CatalogResult<ContentTopicsView>(new(id, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", [Topic])), token); }
    }
}
