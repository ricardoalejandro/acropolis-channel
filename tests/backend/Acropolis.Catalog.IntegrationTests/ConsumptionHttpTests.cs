using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acropolis.Catalog.Application;
using Acropolis.Catalog.Infrastructure;
using Acropolis.Identity.Infrastructure;
using Acropolis.Migrations;
using Acropolis.Subscriptions.Application;
using Acropolis.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acropolis.Catalog.IntegrationTests;

[Collection("CatalogPostgres")]
public sealed class ConsumptionHttpTests(CatalogFixture database)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Password = "Consumption synthetic phrase 2026";
    private static CreateContentRequest Reading => new("obra-http-privada", "Una obra", "Resumen público", "Sinopsis pública", "lecturas", Author: "Autora pública", Tags: ["Filosofía"], WorkText: "HTTP RESTRICTED WORK. Dos < tres.");
    private static UpdateContentRequest Change(AdminContentView x, string status) => new(x.Version, status, x.Slug, x.Title, x.Summary, x.Body, x.Category, x.CoverAsset, x.DurationSeconds, x.Author, x.Tags, x.WorkText, x.YouTubeId, x.CollectionKind, x.ItemIds);
    [Fact]
    public async Task ConsumptionRequiresLiveConfirmedAccountAndActiveSubscriptionWithNoAnonymousLeaks()
    {
        await database.ResetAsync(Token);
        var access = new MutableAccess();
        await using var api = new RestrictedApiFactory(database, access);
        using var anonymous = Client(api);
        AdminContentView reading;
        await using (var context = database.Context())
        {
            var service = new CatalogService(context, TimeProvider.System);
            var draft = (await service.CreateAsync(Guid.NewGuid(), Reading, Token)).Value!;
            reading = (await service.UpdateAsync(Guid.NewGuid(), draft.Id, Change(draft, "published"), Token)).Value!;
        }
        using var publicResponse = await anonymous.GetAsync("/api/v1/catalog/content/" + reading.Slug, Token);
        var publicJson = await publicResponse.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(Reading.Body, publicJson.GetProperty("body").GetString());
        Assert.False(publicJson.TryGetProperty("workText", out _)); Assert.False(publicJson.TryGetProperty("youTubeId", out _));
        Assert.DoesNotContain("HTTP RESTRICTED WORK", await publicResponse.Content.ReadAsStringAsync(Token));
        var route = "/api/v1/consumption/content/" + reading.Slug;
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route, Token)).StatusCode);
        var account = await CreateAccount(api, "consumption-user@example.test");
        using var member = Client(api);
        using var login = await MfaTestClient.Login(member, account.Email!, Password, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var denied = await member.GetAsync(route, Token);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("subscription_required", (await denied.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString());
        Assert.DoesNotContain("HTTP RESTRICTED WORK", await denied.Content.ReadAsStringAsync(Token));
        access.Enabled = true;
        using var allowed = await member.GetAsync(route, Token);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Contains("no-store", allowed.Headers.CacheControl!.ToString());
        var work = (await allowed.Content.ReadFromJsonAsync<ContentWorkView>(Token))!;
        Assert.Equal(Reading.WorkText, work.WorkText); Assert.Null(work.YouTubeId); Assert.Empty(work.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync("/api/v1/consumption/content/Invalid", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync("/api/v1/consumption/content/missing", Token)).StatusCode);
        access.Enabled = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(route, Token)).StatusCode); // Eligibility is not cached in the cookie.
        access.Enabled = true;
        await using (var context = database.Context())
            Assert.True((await new CatalogService(context, TimeProvider.System).UpdateAsync(Guid.NewGuid(), reading.Id, Change(reading, "draft"), Token)).Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync(route, Token)).StatusCode);
        using (var scope = api.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>();
            var user = (await manager.FindByIdAsync(account.Id.ToString()))!;
            user.EmailConfirmed = false;
            Assert.True((await manager.UpdateAsync(user)).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync(route, Token)).StatusCode);
    }
    [Fact]
    public async Task EditorialAuditAndRestrictedFieldsRequireContentPermissionMfaAndReadonlyValidatedPagination()
    {
        await database.ResetAsync(Token);
        await using var api = new RestrictedApiFactory(database, new MutableAccess());
        using var anonymous = Client(api);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/content/audit", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/content/" + Guid.NewGuid() + "/summary", Token)).StatusCode);
        var ordinary = await CreateAccount(api, "audit-member@example.test");
        using var member = Client(api);
        using var ordinaryLogin = await MfaTestClient.Login(member, ordinary.Email!, Password, Token);
        Assert.Equal(HttpStatusCode.OK, ordinaryLogin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/v1/admin/content/audit", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/v1/admin/content/" + Guid.NewGuid() + "/summary", Token)).StatusCode);
        var editor = await CreateAccount(api, "audit-editor@example.test");
        var options = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityRegistration.ConfigureDatabase(options, database.MigrationConnection);
        await using (var context = new IdentityDbContext(options.Options))
            await new IdentityOperations(context, TimeProvider.System).SetContentManagerAsync(editor.Email!, true, Token);
        using var client = Client(api);
        using var editorLogin = await MfaTestClient.Login(client, editor.Email!, Password, Token);
        Assert.Equal(HttpStatusCode.OK, editorLogin.StatusCode);
        using var invalidMedia = await Write(client, HttpMethod.Post, "/api/v1/admin/content", Reading with { Category = "podcast", WorkText = null, YouTubeId = "https://youtu.be/dQw4w9WgXcQ" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidMedia.StatusCode);
        Assert.True((await invalidMedia.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("fieldErrors").TryGetProperty("youTubeId", out _));
        using var created = await Write(client, HttpMethod.Post, "/api/v1/admin/content", Reading);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = (await created.Content.ReadFromJsonAsync<AdminContentView>(Token))!;
        Assert.Equal(Reading.WorkText, draft.WorkText);
        using var summaryResponse = await client.GetAsync("/api/v1/admin/content/" + draft.Id + "/summary", Token);
        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        var summary = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(draft.Id, summary.GetProperty("id").GetGuid());
        foreach (var field in new[] { "body", "workText", "youTubeId", "itemIds" }) Assert.False(summary.TryGetProperty(field, out _));
        Assert.DoesNotContain("HTTP RESTRICTED WORK", await summaryResponse.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/admin/content/" + Guid.NewGuid() + "/summary", Token)).StatusCode);
        using var published = await Write(client, HttpMethod.Put, "/api/v1/admin/content/" + draft.Id, Change(draft, "published"));
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        using var auditResponse = await client.GetAsync("/api/v1/admin/content/audit?contentId=" + draft.Id + "&action=content.published&pageSize=1", Token);
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        var audit = (await auditResponse.Content.ReadFromJsonAsync<ContentAuditPage>(Token))!;
        Assert.Equal(1, audit.Total); Assert.Equal(draft.Id, Assert.Single(audit.Items).ContentId);
        Assert.DoesNotContain("HTTP RESTRICTED WORK", await auditResponse.Content.ReadAsStringAsync(Token));
        var detail = (await client.GetFromJsonAsync<ContentAuditPage>("/api/v1/admin/content/" + draft.Id + "/audit?page=1&pageSize=1", Token))!;
        Assert.Equal(2, detail.Total); Assert.Single(detail.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/admin/content/" + Guid.NewGuid() + "/audit", Token)).StatusCode);
        foreach (var query in new[] { "page=0", "pageSize=101", "action=anything", "contentId=" + Guid.Empty, "fromUtc=2026-10-06T00%3A00%3A00Z&toUtc=2026-10-05T00%3A00%3A00Z", "fromUtc=2026-10-06T00%3A00%3A00%2B05%3A00" })
        {
            using var invalid = await client.GetAsync("/api/v1/admin/content/audit?" + query, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("validation_error", (await invalid.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/admin/content/" + draft.Id + "/audit?page=0", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/v1/admin/content/audit", Token)).StatusCode);
    }
    private static HttpClient Client(WebApplicationFactory<Program> api) => api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    private async Task<HttpResponseMessage> Write<T>(HttpClient client, HttpMethod method, string path, T body)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf", Token);
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        return await client.SendAsync(request, Token);
    }
    private async Task<ChannelUser> CreateAccount(WebApplicationFactory<Program> api, string email)
    {
        using var scope = api.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>();
        var user = new ChannelUser { Id = Guid.NewGuid(), Email = email, UserName = email, DisplayName = "Synthetic consumption user", EmailConfirmed = true };
        Assert.True((await manager.CreateAsync(user, Password)).Succeeded);
        return user;
    }
    private sealed class MutableAccess : ISubscriptionAccess
    {
        public bool Enabled { get; set; }
        public Task<bool> HasActiveAsync(Guid userId, CancellationToken token) => Task.FromResult(Enabled);
    }
    private sealed class RestrictedApiFactory(CatalogFixture fixture, MutableAccess access) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = fixture.RuntimeConnection, ["Identity:EmailEnabled"] = "false" }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISubscriptionAccess>();
                services.AddSingleton<ISubscriptionAccess>(access);
            });
        }
    }
}
