using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acropolis.Catalog.Application;
using Acropolis.Catalog.Infrastructure;
using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;
using Acropolis.Migrations;
using Acropolis.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Acropolis.Catalog.IntegrationTests;

[Collection("CatalogPostgres")]
public sealed class CatalogHttpTests(CatalogFixture database)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Password = "Acropolis catalog phrase";
    private static CreateContentRequest Draft => new("conoce-la-filosofia", "Conoce la filosofía", "Resumen público", "Sinopsis editorial pública. Dos < tres.\nSin obra completa.", "videos", "editorial-dialogue", 600);
    private static UpdateContentRequest Change(AdminContentView view, string status) => new(view.Version, status, view.Slug, view.Title, view.Summary, view.Body, view.Category, view.CoverAsset, view.DurationSeconds);

    [Fact]
    public async Task AnonymousPublishedContractAndQueryErrorsDoNotExposeAdministrativeOrMediaFields()
    {
        await database.ResetAsync(Token);
        await using var api = new CatalogApiFactory(database);
        using var client = Client(api);
        var categories = await client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/categories", Token);
        Assert.Equal(6, categories.GetProperty("items").GetArrayLength());
        Assert.Empty((await client.GetFromJsonAsync<ContentPage>("/api/v1/catalog/content", Token))!.Items);
        await using (var context = database.Context())
        {
            var service = new CatalogService(context, TimeProvider.System);
            var draft = (await service.CreateAsync(Guid.NewGuid(), Draft, Token)).Value!;
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/catalog/content/" + draft.Slug, Token)).StatusCode);
            await service.UpdateAsync(Guid.NewGuid(), draft.Id, Change(draft, "published"), Token);
        }
        using var listResponse = await client.GetAsync("/api/v1/catalog/content?pageSize=3", Token);
        Assert.Equal("no-store", listResponse.Headers.CacheControl!.ToString());
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>(Token);
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.False(item.TryGetProperty("body", out _)); Assert.False(item.TryGetProperty("status", out _)); Assert.False(item.TryGetProperty("version", out _));
        using var detailResponse = await client.GetAsync("/api/v1/catalog/content/" + Draft.Slug, Token);
        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(Draft.Body, detail.GetProperty("body").GetString());
        foreach (var forbidden in new[] { "mediaUrl", "origin", "key", "email", "version", "status", "createdUtc" }) Assert.False(detail.TryGetProperty(forbidden, out _));
        foreach (var url in new[] { "/api/v1/catalog/content/missing", "/api/v1/catalog/content/Invalid" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url, Token)).StatusCode);
        foreach (var query in new[] { "page=0", "pageSize=101", "category=unknown", "search=tema%00", "search=tema%0A", "search=" + new string('a', 101) })
        {
            using var invalid = await client.GetAsync("/api/v1/catalog/content?" + query, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("validation_error", (await invalid.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString());
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/content", Token)).StatusCode);
    }

    [Fact]
    public async Task EditorialCrudRequiresSeparatePermissionRealMfaCsrfAndVersion()
    {
        await database.ResetAsync(Token);
        await using var api = new CatalogApiFactory(database);
        var editor = await CreateAccount(api, "catalog-editor@example.test");
        var ordinary = await CreateAccount(api, "catalog-member@example.test");
        var usersAdmin = await CreateAccount(api, "catalog-users-admin@example.test");
        await using (var migration = IdentityContext())
        {
            var operations = new IdentityOperations(migration, TimeProvider.System);
            await operations.SetContentManagerAsync(editor.Email!, true, Token);
            await operations.BootstrapAsync(usersAdmin.Email!, Token);
        }
        using var memberClient = Client(api);
        using var memberLogin = await MfaTestClient.Login(memberClient, ordinary.Email!, Password, Token);
        Assert.Equal(HttpStatusCode.OK, memberLogin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.GetAsync("/api/v1/admin/content", Token)).StatusCode);
        using var userAdminClient = Client(api);
        using var usersLogin = await MfaTestClient.Login(userAdminClient, usersAdmin.Email!, Password, Token);
        Assert.Equal(HttpStatusCode.OK, usersLogin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await userAdminClient.GetAsync("/api/v1/admin/content", Token)).StatusCode);
        using var client = Client(api);
        using var editorLogin = await MfaTestClient.Login(client, editor.Email!, Password, Token);
        Assert.Equal(HttpStatusCode.OK, editorLogin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/users", Token)).StatusCode);
        using var missingCsrf = await client.PostAsJsonAsync("/api/v1/admin/content", Draft, Token);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        using var overposting = await Write(client, HttpMethod.Post, "/api/v1/admin/content", new { Draft.Slug, Draft.Title, Draft.Summary, Draft.Body, Draft.Category, status = "published", mediaUrl = "https://private.example/key" });
        Assert.Equal(HttpStatusCode.BadRequest, overposting.StatusCode);
        using var invalid = await Write(client, HttpMethod.Post, "/api/v1/admin/content", Draft with { Title = "", CoverAsset = "https://arbitrary.example/image" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var fields = (await invalid.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("fieldErrors");
        Assert.True(fields.TryGetProperty("title", out _)); Assert.True(fields.TryGetProperty("coverAsset", out _));
        using var invalidSlug = await Write(client, HttpMethod.Post, "/api/v1/admin/content", Draft with { Slug = "dos\n" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidSlug.StatusCode);
        Assert.True((await invalidSlug.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("fieldErrors").TryGetProperty("slug", out _));
        using var created = await Write(client, HttpMethod.Post, "/api/v1/admin/content", Draft);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = (await created.Content.ReadFromJsonAsync<AdminContentView>(Token))!;
        Assert.Equal("/api/v1/admin/content/" + item.Id, created.Headers.Location!.OriginalString);
        Assert.Equal("draft", item.Status); Assert.Null(item.PublishedUtc);
        var adminPage = (await client.GetFromJsonAsync<AdminContentPage>("/api/v1/admin/content?status=draft&category=videos&search=filosofía", Token))!;
        Assert.Equal(item.Id, Assert.Single(adminPage.Items).Id);
        using var adminList = await client.GetAsync("/api/v1/admin/content", Token);
        Assert.False((await adminList.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("items")[0].TryGetProperty("body", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/admin/content?status=unknown", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/admin/content/" + Guid.NewGuid(), Token)).StatusCode);
        Assert.Equal(item.Id, (await client.GetFromJsonAsync<AdminContentView>("/api/v1/admin/content/" + item.Id, Token))!.Id);
        using var originRejected = await Write(client, HttpMethod.Put, "/api/v1/admin/content/" + item.Id, Change(item, "published"), "https://foreign.example");
        Assert.Equal(HttpStatusCode.BadRequest, originRejected.StatusCode);
        using var invalidVersion = await Write(client, HttpMethod.Put, "/api/v1/admin/content/" + item.Id, Change(item, "published") with { Version = item.Version + "\n" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidVersion.StatusCode);
        Assert.True((await invalidVersion.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("fieldErrors").TryGetProperty("version", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/admin/content?search=tema%00", Token)).StatusCode);
        using var publishedResponse = await Write(client, HttpMethod.Put, "/api/v1/admin/content/" + item.Id, Change(item, "published"));
        Assert.Equal(HttpStatusCode.OK, publishedResponse.StatusCode);
        var published = (await publishedResponse.Content.ReadFromJsonAsync<AdminContentView>(Token))!;
        Assert.NotNull(published.PublishedUtc);
        using var stale = await Write(client, HttpMethod.Put, "/api/v1/admin/content/" + item.Id, Change(item, "published"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("concurrency_conflict", (await stale.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString());
        using var archiveResponse = await Write(client, HttpMethod.Put, "/api/v1/admin/content/" + item.Id, Change(published, "archived"));
        Assert.Equal(HttpStatusCode.OK, archiveResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/catalog/content/" + item.Slug, Token)).StatusCode);
        using var noDelete = await client.DeleteAsync("/api/v1/admin/content/" + item.Id, Token);
        Assert.Equal(HttpStatusCode.NotFound, noDelete.StatusCode);
        await using var revoke = IdentityContext();
        await new IdentityOperations(revoke, TimeProvider.System).SetContentManagerAsync(editor.Email!, false, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/content", Token)).StatusCode);
    }

    private static HttpClient Client(CatalogApiFactory api) => api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    private async Task<HttpResponseMessage> Write<T>(HttpClient client, HttpMethod method, string url, T body, string? origin = null)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf", Token);
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await client.SendAsync(request, Token);
    }
    private IdentityDbContext IdentityContext()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<IdentityDbContext>();
        IdentityRegistration.ConfigureDatabase(options, database.MigrationConnection);
        return new(options.Options);
    }
    private async Task<ChannelUser> CreateAccount(CatalogApiFactory api, string email)
    {
        using var scope = api.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>();
        var user = new ChannelUser { Id = Guid.NewGuid(), Email = email, UserName = email, DisplayName = "QA Catalog account", EmailConfirmed = true, Levels = [new UserLevel { Level = "Instructor" }] };
        user.Levels[0].UserId = user.Id;
        Assert.True((await manager.CreateAsync(user, Password)).Succeeded);
        return user;
    }
}
