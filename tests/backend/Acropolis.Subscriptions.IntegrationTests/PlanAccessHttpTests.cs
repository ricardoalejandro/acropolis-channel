using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acropolis.Catalog.Application;
using Acropolis.Catalog.Infrastructure;
using Acropolis.Identity.Infrastructure;
using Acropolis.Identity.IntegrationTests;
using Acropolis.Migrations;
using Acropolis.Subscriptions.Application;
using Acropolis.Subscriptions.Infrastructure;
using Acropolis.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acropolis.Subscriptions.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class PlanAccessHttpTests(IdentityFixture database)
{
    private const string Password = "Plan access synthetic phrase 2026";
    private const string PrivateText = "Synthetic protected plan matrix reading.";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FreeScopeRechecksEditorialMarkBeforeWorkVersionAndStartOrPulseReplay()
    {
        await database.ResetAsync(Token);
        await using var fixtureApi = new IdentityApiFactory(database);
        await using var api = RecordingApi(fixtureApi);
        await AssertRealServicesAsync(api);
        var member = await AccountAsync(api, "free-matrix-member");
        var manager = await AccountAsync(api, "free-matrix-manager", subscriptions: true, content: true);
        using var client = Client(api); using var editor = Client(api); using var anonymous = Client(api);
        await LoginAsync(client, member); await LoginAsync(editor, manager);
        var free = await PublishAsync(editor, "free-matrix-reading", true);
        var paid = await PublishAsync(editor, "paid-matrix-reading", null);
        Assert.False(paid.GetProperty("isFree").GetBoolean());
        var metadata = await GetAsync(anonymous, $"/api/v1/catalog/content/{free.GetProperty("slug").GetString()}");
        Assert.True(metadata.GetProperty("isFree").GetBoolean());
        Assert.False(metadata.TryGetProperty("workText", out _));
        await ErrorAsync(anonymous, HttpMethod.Get, WorkRoute(free), null, HttpStatusCode.Unauthorized);
        await ErrorAsync(client, HttpMethod.Get, WorkRoute(free), null, HttpStatusCode.Forbidden, "subscription_required");
        Assert.Equal(0, (await RecordingFactsAsync(api))[0]);
        var activation = await OkAsync(await WriteAsync(client, HttpMethod.Post, "/api/v1/subscriptions/activate", new { }));
        Assert.Equal("free_beta", activation.GetProperty("plan").GetString());
        Assert.Equal("active", activation.GetProperty("effectiveState").GetString());
        Assert.Equal(JsonValueKind.Null, activation.GetProperty("expiresUtc").ValueKind);
        Assert.True((await GetAsync(client, "/api/v1/consumption/capabilities")).GetProperty("recordingEnabled").GetBoolean());
        Assert.Equal(PrivateText, (await GetAsync(client, WorkRoute(free))).GetProperty("workText").GetString());
        await ErrorAsync(client, HttpMethod.Get, WorkRoute(paid), null, HttpStatusCode.Forbidden, "content_requires_plan");
        var course = await PublishAsync(editor, "free-matrix-course", true, paid.GetProperty("id").GetGuid());
        var courseWork = await GetAsync(client, WorkRoute(course));
        var child = Assert.Single(courseWork.GetProperty("items").EnumerateArray());
        Assert.Equal(paid.GetProperty("id").GetGuid(), child.GetProperty("id").GetGuid());
        Assert.False(child.GetProperty("isFree").GetBoolean());
        Assert.False(child.TryGetProperty("workText", out _));
        Assert.False(child.TryGetProperty("youTubeId", out _));
        Assert.DoesNotContain(PrivateText, courseWork.GetRawText());
        await ErrorAsync(client, HttpMethod.Get, WorkRoute(paid), null, HttpStatusCode.Forbidden, "content_requires_plan");
        var start = new StartConsumptionRequest(Guid.NewGuid(), free.GetProperty("version").GetString()!);
        var session = await OkAsync(await WriteAsync(client, HttpMethod.Post, StartRoute(free), start));
        fixtureApi.Clock.Advance(TimeSpan.FromSeconds(1));
        var pulse = ReadingPulse();
        await OkAsync(await WriteAsync(client, HttpMethod.Post, PulseRoute(session), pulse));
        var recorded = await RecordingFactsAsync(api);
        var restricted = await OkAsync(await WriteAsync(editor, HttpMethod.Put, EditorRoute(free), EditorialUpdate(free, false)));
        Assert.NotEqual(free.GetProperty("version").GetString(), restricted.GetProperty("version").GetString());
        await ErrorAsync(client, HttpMethod.Get, WorkRoute(free), null, HttpStatusCode.Forbidden, "content_requires_plan");
        await ErrorAsync(client, HttpMethod.Post, StartRoute(free), start, HttpStatusCode.Forbidden, "content_requires_plan");
        await ErrorAsync(client, HttpMethod.Post, PulseRoute(session), pulse, HttpStatusCode.Forbidden, "content_requires_plan");
        Assert.Equal(recorded, await RecordingFactsAsync(api));
        var reopened = await OkAsync(await WriteAsync(editor, HttpMethod.Put, EditorRoute(free), EditorialUpdate(restricted, true)));
        Assert.Equal(PrivateText, (await GetAsync(client, WorkRoute(reopened))).GetProperty("workText").GetString());
        await OkAsync(await WriteAsync(client, HttpMethod.Post, StartRoute(reopened), new StartConsumptionRequest(Guid.NewGuid(), reopened.GetProperty("version").GetString()!)));
        Assert.Equal(recorded[0] + 1, (await RecordingFactsAsync(api))[0]);
    }

    [Theory]
    [InlineData("probationismo")]
    [InlineData("annual")]
    public async Task FutureCalendarPlanStartsInclusivelyAndExpiresExclusivelyWithoutAutomaticFreeFallback(string plan)
    {
        await database.ResetAsync(Token);
        await using var fixtureApi = new IdentityApiFactory(database);
        await using var api = RecordingApi(fixtureApi);
        await AssertRealServicesAsync(api);
        var member = await AccountAsync(api, "future-matrix-member");
        var manager = await AccountAsync(api, "future-matrix-manager", subscriptions: true, content: true);
        using var client = Client(api); using var editor = Client(api);
        await LoginAsync(client, member); await LoginAsync(editor, manager);
        var free = await PublishAsync(editor, "future-free-reading", true);
        var paid = await PublishAsync(editor, "future-paid-reading", false);
        var starts = WholeSecond(fixtureApi.Clock.GetUtcNow()).AddMinutes(1);
        var expires = EndOfPeriod(plan, starts);
        var assigned = await AssignAsync(editor, member.Id, plan, starts, null);
        Assert.Equal("scheduled", assigned.GetProperty("effectiveState").GetString());
        Assert.Equal(starts, assigned.GetProperty("startsUtc").GetDateTimeOffset());
        Assert.Equal(expires, assigned.GetProperty("expiresUtc").GetDateTimeOffset());
        await ErrorAsync(client, HttpMethod.Get, WorkRoute(free), null, HttpStatusCode.Forbidden, "subscription_required");
        await ErrorAsync(client, HttpMethod.Get, WorkRoute(paid), null, HttpStatusCode.Forbidden, "subscription_required");
        fixtureApi.Clock.Advance(starts - fixtureApi.Clock.GetUtcNow());
        Assert.Equal("active", (await MineAsync(client)).GetProperty("effectiveState").GetString());
        Assert.Equal(PrivateText, (await GetAsync(client, WorkRoute(free))).GetProperty("workText").GetString());
        Assert.Equal(PrivateText, (await GetAsync(client, WorkRoute(paid))).GetProperty("workText").GetString());
        var start = new StartConsumptionRequest(Guid.NewGuid(), paid.GetProperty("version").GetString()!);
        var session = await OkAsync(await WriteAsync(client, HttpMethod.Post, StartRoute(paid), start));
        fixtureApi.Clock.Advance(TimeSpan.FromSeconds(1));
        await OkAsync(await WriteAsync(client, HttpMethod.Post, PulseRoute(session), ReadingPulse()));
        fixtureApi.Clock.Advance(expires - fixtureApi.Clock.GetUtcNow());
        // A fresh ordinary login separates plan expiry from the independent eight-hour authentication lifetime.
        using var expiredClient = Client(api); await LoginAsync(expiredClient, member);
        var expired = await MineAsync(expiredClient);
        Assert.Equal("active", expired.GetProperty("status").GetString());
        Assert.Equal("expired", expired.GetProperty("effectiveState").GetString());
        Assert.Equal(plan, expired.GetProperty("plan").GetString());
        Assert.Equal(assigned.GetProperty("version").GetString(), expired.GetProperty("version").GetString());
        Assert.Equal(expires, expired.GetProperty("expiresUtc").GetDateTimeOffset());
        var recorded = await RecordingFactsAsync(api);
        await ErrorAsync(expiredClient, HttpMethod.Get, WorkRoute(free), null, HttpStatusCode.Forbidden, "subscription_required");
        await ErrorAsync(expiredClient, HttpMethod.Get, WorkRoute(paid), null, HttpStatusCode.Forbidden, "subscription_required");
        await ErrorAsync(expiredClient, HttpMethod.Post, StartRoute(paid), start, HttpStatusCode.Forbidden, "subscription_required");
        await ErrorAsync(expiredClient, HttpMethod.Post, PulseRoute(session), ReadingPulse(), HttpStatusCode.Forbidden, "subscription_required");
        Assert.Equal(recorded, await RecordingFactsAsync(api));
    }

    [Theory]
    [InlineData("probationismo")]
    [InlineData("annual")]
    public async Task EarlyManualRenewalPreservesPresentAccessAndOldBoundaryWhileVersionConflictsCannotExtendAgain(string plan)
    {
        await database.ResetAsync(Token);
        await using var fixtureApi = new IdentityApiFactory(database);
        await using var api = RecordingApi(fixtureApi);
        await AssertRealServicesAsync(api);
        var member = await AccountAsync(api, "renewal-matrix-member");
        var manager = await AccountAsync(api, "renewal-matrix-manager", subscriptions: true, content: true);
        using var client = Client(api); using var editor = Client(api);
        await LoginAsync(client, member); await LoginAsync(editor, manager);
        var paid = await PublishAsync(editor, "renewal-paid-reading", false);
        var starts = WholeSecond(fixtureApi.Clock.GetUtcNow());
        var oldEnd = EndOfPeriod(plan, starts);
        var newEnd = EndOfPeriod(plan, oldEnd);
        var assigned = await AssignAsync(editor, member.Id, plan, starts, null);
        Assert.Equal(PrivateText, (await GetAsync(client, WorkRoute(paid))).GetProperty("workText").GetString());
        var session = await OkAsync(await WriteAsync(client, HttpMethod.Post, StartRoute(paid), new StartConsumptionRequest(Guid.NewGuid(), paid.GetProperty("version").GetString()!)));
        fixtureApi.Clock.Advance(TimeSpan.FromSeconds(1));
        var firstPulse = await OkAsync(await WriteAsync(client, HttpMethod.Post, PulseRoute(session), ReadingPulse()));
        var recorded = await RecordingFactsAsync(api);
        var renewed = await AssignAsync(editor, member.Id, plan, oldEnd, assigned.GetProperty("version").GetString());
        Assert.Equal(assigned.GetProperty("id").GetGuid(), renewed.GetProperty("id").GetGuid());
        Assert.Equal(starts, renewed.GetProperty("startsUtc").GetDateTimeOffset());
        Assert.Equal(newEnd, renewed.GetProperty("expiresUtc").GetDateTimeOffset());
        Assert.Equal("active", renewed.GetProperty("effectiveState").GetString());
        Assert.NotEqual(assigned.GetProperty("version").GetString(), renewed.GetProperty("version").GetString());
        Assert.Equal(PrivateText, (await GetAsync(client, WorkRoute(paid))).GetProperty("workText").GetString());
        var replay = await OkAsync(await WriteAsync(client, HttpMethod.Post, PulseRoute(session), ReadingPulse()));
        Assert.Equal(firstPulse.GetRawText(), replay.GetRawText());
        Assert.Equal(recorded, await RecordingFactsAsync(api));
        var audit = await AuditAsync(editor, renewed);
        Assert.Equal(2, audit.GetProperty("total").GetInt32());
        var renewal = Assert.Single(audit.GetProperty("items").EnumerateArray(), item => item.GetProperty("action").GetString() == "subscription.renewed");
        Assert.Equal(manager.Id, renewal.GetProperty("actorId").GetGuid());
        Assert.Equal(oldEnd, renewal.GetProperty("beforeExpiresUtc").GetDateTimeOffset());
        Assert.Equal(newEnd, renewal.GetProperty("afterExpiresUtc").GetDateTimeOffset());
        Assert.Equal(starts, renewal.GetProperty("beforeStartsUtc").GetDateTimeOffset());
        Assert.Equal(starts, renewal.GetProperty("afterStartsUtc").GetDateTimeOffset());
        var auditIds = AuditIds(audit);
        await ErrorAsync(editor, HttpMethod.Post, AssignRoute(member.Id), Assignment(plan, oldEnd, assigned.GetProperty("version").GetString()), HttpStatusCode.Conflict, "concurrency_conflict");
        var unchanged = await AssignAsync(editor, member.Id, plan, starts, renewed.GetProperty("version").GetString());
        Assert.Equal(newEnd, unchanged.GetProperty("expiresUtc").GetDateTimeOffset());
        Assert.Equal(renewed.GetProperty("version").GetString(), unchanged.GetProperty("version").GetString());
        var suffixReplay = await AssignAsync(editor, member.Id, plan, oldEnd, renewed.GetProperty("version").GetString());
        Assert.Equal(newEnd, suffixReplay.GetProperty("expiresUtc").GetDateTimeOffset());
        Assert.Equal(renewed.GetProperty("version").GetString(), suffixReplay.GetProperty("version").GetString());
        var differentPlan = plan == "annual" ? "probationismo" : "annual";
        await ErrorAsync(editor, HttpMethod.Post, AssignRoute(member.Id), Assignment(differentPlan, oldEnd, renewed.GetProperty("version").GetString()), HttpStatusCode.Conflict, "active_period_would_be_replaced");
        Assert.Equal(auditIds, AuditIds(await AuditAsync(editor, renewed)));
        Assert.Equal(newEnd, (await MineAsync(client)).GetProperty("expiresUtc").GetDateTimeOffset());
        fixtureApi.Clock.Advance(oldEnd - fixtureApi.Clock.GetUtcNow());
        using var boundaryClient = Client(api); await LoginAsync(boundaryClient, member);
        Assert.Equal("active", (await MineAsync(boundaryClient)).GetProperty("effectiveState").GetString());
        Assert.Equal(PrivateText, (await GetAsync(boundaryClient, WorkRoute(paid))).GetProperty("workText").GetString());
        var boundaryStart = new StartConsumptionRequest(Guid.NewGuid(), paid.GetProperty("version").GetString()!);
        var boundarySession = await OkAsync(await WriteAsync(boundaryClient, HttpMethod.Post, StartRoute(paid), boundaryStart));
        fixtureApi.Clock.Advance(TimeSpan.FromSeconds(1));
        await OkAsync(await WriteAsync(boundaryClient, HttpMethod.Post, PulseRoute(boundarySession), ReadingPulse()));
        fixtureApi.Clock.Advance(newEnd - fixtureApi.Clock.GetUtcNow());
        using var expiredClient = Client(api); await LoginAsync(expiredClient, member);
        Assert.Equal("expired", (await MineAsync(expiredClient)).GetProperty("effectiveState").GetString());
        recorded = await RecordingFactsAsync(api);
        await ErrorAsync(expiredClient, HttpMethod.Get, WorkRoute(paid), null, HttpStatusCode.Forbidden, "subscription_required");
        await ErrorAsync(expiredClient, HttpMethod.Post, StartRoute(paid), boundaryStart, HttpStatusCode.Forbidden, "subscription_required");
        await ErrorAsync(expiredClient, HttpMethod.Post, PulseRoute(boundarySession), ReadingPulse(), HttpStatusCode.Forbidden, "subscription_required");
        Assert.Equal(recorded, await RecordingFactsAsync(api));
    }

    [Fact]
    public async Task AssignmentRequiresIndependentAuthorityCsrfAndVersionAndExpiredAccessOnlyReturnsByExplicitFreeAssignment()
    {
        await database.ResetAsync(Token);
        await using var fixtureApi = new IdentityApiFactory(database);
        await using var api = RecordingApi(fixtureApi);
        await AssertRealServicesAsync(api);
        var member = await AccountAsync(api, "authority-matrix-member");
        var manager = await AccountAsync(api, "authority-matrix-manager", subscriptions: true, content: true);
        var usersManager = await AccountAsync(api, "authority-users-only", users: true);
        var contentManager = await AccountAsync(api, "authority-content-only", content: true);
        var owner = await AccountAsync(api, "authority-protected-owner");
        await using (var identity = database.Context(true))
            await new IdentityOperations(identity, fixtureApi.Clock).BootstrapOwnerAsync(owner.Email!, owner.Email, Token);
        using var client = Client(api); using var editor = Client(api); using var users = Client(api); using var content = Client(api);
        await LoginAsync(client, member); await LoginAsync(editor, manager); await LoginAsync(users, usersManager); await LoginAsync(content, contentManager);
        var free = await PublishAsync(editor, "authority-free-reading", true);
        var paid = await PublishAsync(editor, "authority-paid-reading", false);
        var starts = WholeSecond(fixtureApi.Clock.GetUtcNow()).AddYears(-1).AddMinutes(1);
        var assignment = Assignment("annual", starts, null);
        await ErrorAsync(client, HttpMethod.Post, AssignRoute(member.Id), assignment, HttpStatusCode.Forbidden);
        await ErrorAsync(users, HttpMethod.Post, AssignRoute(member.Id), assignment, HttpStatusCode.Forbidden);
        await ErrorAsync(content, HttpMethod.Post, AssignRoute(member.Id), assignment, HttpStatusCode.Forbidden);
        using (var noCsrf = await editor.PostAsJsonAsync(AssignRoute(member.Id), assignment, Token)) Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        await ErrorAsync(editor, HttpMethod.Post, AssignRoute(owner.Id), assignment, HttpStatusCode.Conflict, "owner_protected");
        await ErrorAsync(editor, HttpMethod.Post, AssignRoute(member.Id), Assignment("annual", null, null), HttpStatusCode.BadRequest, "validation_error");
        await ErrorAsync(editor, HttpMethod.Post, AssignRoute(member.Id), Assignment("free_beta", starts, null), HttpStatusCode.BadRequest, "validation_error");
        var assigned = await AssignAsync(editor, member.Id, "annual", starts, null);
        var auditIds = AuditIds(await AuditAsync(editor, assigned));
        await ErrorAsync(editor, HttpMethod.Post, AssignRoute(member.Id), assignment, HttpStatusCode.Conflict, "concurrency_conflict");
        Assert.Equal(auditIds, AuditIds(await AuditAsync(editor, assigned)));
        Assert.Equal(PrivateText, (await GetAsync(client, WorkRoute(paid))).GetProperty("workText").GetString());
        var session = await OkAsync(await WriteAsync(client, HttpMethod.Post, StartRoute(paid), new StartConsumptionRequest(Guid.NewGuid(), paid.GetProperty("version").GetString()!)));
        fixtureApi.Clock.Advance(TimeSpan.FromSeconds(1));
        await OkAsync(await WriteAsync(client, HttpMethod.Post, PulseRoute(session), ReadingPulse()));
        fixtureApi.Clock.Advance(assigned.GetProperty("expiresUtc").GetDateTimeOffset() - fixtureApi.Clock.GetUtcNow());
        var recorded = await RecordingFactsAsync(api);
        await ErrorAsync(client, HttpMethod.Get, WorkRoute(free), null, HttpStatusCode.Forbidden, "subscription_required");
        await ErrorAsync(client, HttpMethod.Get, WorkRoute(paid), null, HttpStatusCode.Forbidden, "subscription_required");
        await ErrorAsync(client, HttpMethod.Post, StartRoute(paid), new StartConsumptionRequest(Guid.NewGuid(), paid.GetProperty("version").GetString()!), HttpStatusCode.Forbidden, "subscription_required");
        await ErrorAsync(client, HttpMethod.Post, PulseRoute(session), ReadingPulse(), HttpStatusCode.Forbidden, "subscription_required");
        Assert.Equal(recorded, await RecordingFactsAsync(api));
        var switched = await AssignAsync(editor, member.Id, "free_beta", null, assigned.GetProperty("version").GetString());
        Assert.Equal(assigned.GetProperty("id").GetGuid(), switched.GetProperty("id").GetGuid());
        Assert.Equal("free_beta", switched.GetProperty("plan").GetString());
        Assert.Equal("active", switched.GetProperty("effectiveState").GetString());
        Assert.Equal(JsonValueKind.Null, switched.GetProperty("expiresUtc").ValueKind);
        Assert.Equal(PrivateText, (await GetAsync(client, WorkRoute(free))).GetProperty("workText").GetString());
        await ErrorAsync(client, HttpMethod.Get, WorkRoute(paid), null, HttpStatusCode.Forbidden, "content_requires_plan");
        await ErrorAsync(client, HttpMethod.Post, PulseRoute(session), ReadingPulse(), HttpStatusCode.Forbidden, "content_requires_plan");
        Assert.Equal(recorded, await RecordingFactsAsync(api));
        var audit = await AuditAsync(editor, switched);
        Assert.Equal(2, audit.GetProperty("total").GetInt32());
        var change = Assert.Single(audit.GetProperty("items").EnumerateArray(), item => item.GetProperty("afterPlan").GetString() == "free_beta");
        Assert.Equal("subscription.assigned", change.GetProperty("action").GetString());
        Assert.Equal("annual", change.GetProperty("beforePlan").GetString());
        Assert.Equal(manager.Id, change.GetProperty("actorId").GetGuid());
        Assert.Equal(JsonValueKind.Null, change.GetProperty("afterExpiresUtc").ValueKind);
        var again = await AssignAsync(editor, member.Id, "free_beta", null, switched.GetProperty("version").GetString());
        Assert.Equal(switched.GetProperty("version").GetString(), again.GetProperty("version").GetString());
        Assert.Equal(switched.GetProperty("startsUtc").GetDateTimeOffset(), again.GetProperty("startsUtc").GetDateTimeOffset());
        Assert.Equal(AuditIds(audit), AuditIds(await AuditAsync(editor, again)));
    }

    private static WebApplicationFactory<Program> RecordingApi(IdentityApiFactory fixtureApi) =>
        fixtureApi.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<ConsumptionRecordingOptions>(settings => settings.RecordingEnabled = true)));
    private static HttpClient Client(WebApplicationFactory<Program> api) => api.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });
    private static async Task AssertRealServicesAsync(WebApplicationFactory<Program> api)
    {
        Assert.Equal("Testing", api.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        await using var scope = api.Services.CreateAsyncScope();
        Assert.IsType<SubscriptionService>(scope.ServiceProvider.GetRequiredService<ISubscriptionAccess>());
        Assert.IsType<CatalogService>(scope.ServiceProvider.GetRequiredService<ICatalogService>());
        Assert.IsType<ConsumptionRecordingService>(scope.ServiceProvider.GetRequiredService<IConsumptionRecordingService>());
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.StartsWith("acropolis_test_", catalog.Database.GetDbConnection().Database);
        Assert.Equal("acropolis_app", await catalog.Database.SqlQueryRaw<string>("SELECT current_user AS \"Value\"").SingleAsync(Token));
    }
    private static async Task<ChannelUser> AccountAsync(WebApplicationFactory<Program> api, string name, bool subscriptions = false, bool content = false, bool users = false)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>();
        var account = new ChannelUser
        {
            Id = Guid.NewGuid(),
            Email = name + "@example.test",
            UserName = name + "@example.test",
            DisplayName = "Synthetic plan account",
            EmailConfirmed = true,
            LockoutEnabled = true,
            SubscriptionsManage = subscriptions,
            ContentManage = content,
            UsersManage = users
        };
        Assert.True((await manager.CreateAsync(account, Password)).Succeeded);
        return account;
    }
    private static async Task LoginAsync(HttpClient client, ChannelUser account)
    {
        using var login = await MfaTestClient.Login(client, account.Email!, Password, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }
    private static async Task<HttpResponseMessage> WriteAsync(HttpClient client, HttpMethod method, string route, object body)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf", Token);
        using var request = new HttpRequestMessage(method, route) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        return await client.SendAsync(request, Token);
    }
    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).Clone();
        }
    }
    private static async Task<JsonElement> GetAsync(HttpClient client, string route) => await OkAsync(await client.GetAsync(route, Token));
    private static async Task ErrorAsync(HttpClient client, HttpMethod method, string route, object? body, HttpStatusCode status, string? code = null)
    {
        using var response = body is null ? await client.GetAsync(route, Token) : await WriteAsync(client, method, route, body);
        Assert.Equal(status, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain(PrivateText, text);
        if (code is not null)
        {
            using var document = JsonDocument.Parse(text);
            Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        }
    }
    private static async Task<JsonElement> PublishAsync(HttpClient editor, string slug, bool? isFree, Guid? child = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["slug"] = slug,
            ["title"] = "Synthetic plan reading",
            ["summary"] = "Synthetic public synopsis.",
            ["body"] = "Synthetic public editorial text.",
            ["category"] = child is null ? "lecturas" : "cursos",
            ["workText"] = child is null ? PrivateText : null
        };
        if (isFree.HasValue) request["isFree"] = isFree.Value;
        if (child.HasValue) { request["collectionKind"] = "course"; request["itemIds"] = new[] { child.Value }; }
        var draft = await OkAsync(await WriteAsync(editor, HttpMethod.Post, "/api/v1/admin/content", request), HttpStatusCode.Created);
        request["version"] = draft.GetProperty("version").GetString(); request["status"] = "published";
        return await OkAsync(await WriteAsync(editor, HttpMethod.Put, EditorRoute(draft), request));
    }
    private static object EditorialUpdate(JsonElement content, bool isFree) => new
    {
        version = content.GetProperty("version").GetString(),
        status = "published",
        slug = content.GetProperty("slug").GetString(),
        title = content.GetProperty("title").GetString(),
        summary = "Synthetic public synopsis.",
        body = "Synthetic public editorial text.",
        category = "lecturas",
        workText = PrivateText,
        isFree
    };
    private static string EditorRoute(JsonElement content) => $"/api/v1/admin/content/{content.GetProperty("id").GetGuid()}";
    private static string WorkRoute(JsonElement content) => $"/api/v1/consumption/content/{content.GetProperty("slug").GetString()}";
    private static string StartRoute(JsonElement content) => WorkRoute(content) + "/sessions";
    private static string PulseRoute(JsonElement session) => $"/api/v1/consumption/sessions/{session.GetProperty("sessionId").GetGuid()}/pulses";
    private static string AssignRoute(Guid userId) => $"/api/v1/admin/subscriptions/accounts/{userId}/assign";
    private static object Assignment(string plan, DateTimeOffset? startsUtc, string? version) => new { plan, startsUtc, version, reason = "Synthetic manual assignment or renewal" };
    private static async Task<JsonElement> AssignAsync(HttpClient editor, Guid userId, string plan, DateTimeOffset? startsUtc, string? version) =>
        await OkAsync(await WriteAsync(editor, HttpMethod.Post, AssignRoute(userId), Assignment(plan, startsUtc, version)));
    private static async Task<JsonElement> MineAsync(HttpClient client) => (await GetAsync(client, "/api/v1/subscriptions/me")).GetProperty("subscription");
    private static async Task<JsonElement> AuditAsync(HttpClient editor, JsonElement subscription) =>
        await GetAsync(editor, $"/api/v1/admin/subscriptions/audit?subscriptionId={subscription.GetProperty("id").GetGuid()}&pageSize=100");
    private static string[] AuditIds(JsonElement audit) => audit.GetProperty("items").EnumerateArray()
        .Select(item => item.GetProperty("id").GetGuid().ToString("N")).Order(StringComparer.Ordinal).ToArray();
    private static DateTimeOffset WholeSecond(DateTimeOffset value) => DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());
    private static DateTimeOffset EndOfPeriod(string plan, DateTimeOffset starts) => plan == "annual" ? starts.AddYears(1) : starts.AddMonths(3);
    private static ConsumptionPulseRequest ReadingPulse() => new(1, 1000, 1000, Reading: new(5000, [new(0, 5000)]));
    private static async Task<long[]> RecordingFactsAsync(WebApplicationFactory<Program> api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        return
        [
            await catalog.ConsumptionSessions.LongCountAsync(Token), await catalog.ConsumptionPulses.LongCountAsync(Token),
            await catalog.ConsumptionDaily.SumAsync(row => row.Starts, Token), await catalog.ConsumptionDaily.SumAsync(row => row.RecordedPulses, Token),
            await catalog.ConsumptionDaily.SumAsync(row => row.CreditedMs, Token), await catalog.ConsumptionAccountDaily.SumAsync(row => row.Starts, Token),
            await catalog.ConsumptionAccountDaily.SumAsync(row => row.RecordedPulses, Token), await catalog.ConsumptionAccountDaily.SumAsync(row => row.CreditedMs, Token)
        ];
    }
}
