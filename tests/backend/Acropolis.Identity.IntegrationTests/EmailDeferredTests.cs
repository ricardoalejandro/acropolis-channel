using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Acropolis.Identity.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class EmailDeferredTests(IdentityFixture database)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Password = "Email deferred synthetic phrase 2026";

    [Fact]
    public async Task DeferredEmailDoesNotCreateAccountsTokensOrDeliveryAttemptsAndKeepsConfirmedLogin()
    {
        await database.ResetAsync(Token);
        await using var api = new IdentityApiFactory(database);
        using var enabled = api.Client();
        const string email = "deferred@example.test";
        Assert.Equal(HttpStatusCode.Accepted, (await Write(enabled, "register", new RegisterRequest("Persona", email, Password))).StatusCode);
        await using var disabled = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<IdentitySettings>(settings => settings.EmailEnabled = false)));
        using var client = disabled.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var capabilities = await client.GetAsync("/api/v1/identity/capabilities", Token);
        Assert.False((await capabilities.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("emailEnabled").GetBoolean());
        Assert.Contains("no-store", capabilities.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        foreach (var route in new[] { "register", "resend-confirmation", "forgot-password" })
        {
            object input = route == "register" ? new RegisterRequest("Otra persona", "other@example.test", Password) : new EmailRequest(email);
            using var response = await Write(client, route, input);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("email_unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString());
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/identity/register", new RegisterRequest("Persona", "other@example.test", Password), Token)).StatusCode);
        await using (var scope = disabled.Services.CreateAsyncScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync(Token));
        Assert.Empty(api.Mailer.Messages);
        await using (var context = database.Context())
        {
            Assert.Equal(1, await context.Users.CountAsync(Token));
            Assert.False((await context.Users.SingleAsync(Token)).EmailConfirmed);
            Assert.Equal(1, await context.Flows.CountAsync(Token));
            Assert.Equal(0, (await context.Outbox.SingleAsync(Token)).Attempts);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Write(client, "login", new LoginRequest(email, Password))).StatusCode);
        await api.DispatchAsync();
        var confirmation = Assert.Single(api.Mailer.Messages);
        Assert.Equal(HttpStatusCode.NoContent, (await Write(enabled, "confirm-email", new ConfirmEmailRequest(confirmation.UserId, confirmation.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Write(client, "login", new LoginRequest(email, Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/identity/me", Token)).StatusCode);
    }

    private static async Task<HttpResponseMessage> Write(HttpClient client, string route, object body)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf", Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/identity/" + route) { Content = JsonContent.Create(body, body.GetType()) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        return await client.SendAsync(request, Token);
    }
}
