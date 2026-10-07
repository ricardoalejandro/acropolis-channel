using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;
using Acropolis.Identity.IntegrationTests;
using Acropolis.Subscriptions.Application;
using Acropolis.Subscriptions.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Acropolis.Subscriptions.IntegrationTests;

// Real host, Identity recipient lookup, subscription services and PostgreSQL.
// Only SMTP transport is captured; no worker runs or production configuration is loaded.
internal sealed class SubscriptionNotificationTestHost : IAsyncDisposable
{
    public const string Password = "Synthetic notification account phrase 2026";
    private readonly IdentityApiFactory origin;
    private readonly WebApplicationFactory<Program> api;
    public CapturedSubscriptionSender Sender { get; }
    public TestClock Clock => origin.Clock;
    public IServiceProvider Services => api.Services;
    public SubscriptionNotificationOptions Options => Services.GetRequiredService<IOptions<SubscriptionNotificationOptions>>().Value;
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public SubscriptionNotificationTestHost(IdentityFixture database, bool enabled = true, bool available = true)
    {
        origin = new(database);
        origin.Clock.Advance(TimeSpan.FromTicks(-(origin.Clock.GetUtcNow().Ticks % 10)));
        Sender = new() { IsAvailable = available };
        api = origin.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<SubscriptionNotificationOptions>(options => { options.Enabled = enabled; options.RunInTesting = false; });
            services.RemoveAll<ITransactionalEmailSender>();
            services.AddSingleton<ITransactionalEmailSender>(Sender);
            foreach (var worker in services.Where(registration => registration.ServiceType == typeof(IHostedService) &&
                registration.ImplementationType?.Namespace == "Acropolis.Subscriptions.Infrastructure").ToArray()) services.Remove(worker);
        }));
    }
    public HttpClient Client() => api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    public async Task<ChannelUser> AccountAsync(string suffix, bool manager = false, bool confirmed = true)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>();
        var email = suffix + "@example.test";
        var user = new ChannelUser { Id = Guid.NewGuid(), Email = email, UserName = email, DisplayName = "QA notification holder", EmailConfirmed = confirmed, SubscriptionsManage = manager };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        return user;
    }
    public async Task<SubscriptionResult<SubscriptionView>> AssignAsync(Guid actor, Guid user, string plan, DateTimeOffset? starts, string? version = null)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISubscriptionService>().AssignAsync(actor, user,
            new(plan, starts, version, "Independent synthetic notification assignment"), Token);
    }
    public async Task<SubscriptionResult<SubscriptionView>> UpdateAsync(Guid actor, SubscriptionView row, string status)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISubscriptionService>().UpdateAsync(actor, row.Id,
            new(row.Version, status, "Independent synthetic notification status"), Token);
    }
    public async Task<bool> DispatchAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SubscriptionNotificationDispatcher>().DispatchAsync(Token);
    }
    public async Task<int> ScheduleAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SubscriptionNotificationScheduler>().ScheduleAsync(Token);
    }
    public async Task<SubscriptionView> CurrentAsync(Guid user)
    {
        await using var scope = Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ISubscriptionService>().GetMineAsync(user, Token)).Subscription!;
    }
    public static async Task<HttpResponseMessage> WriteAsync(HttpClient client, string route, object body)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf", Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body, body.GetType()) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        return await client.SendAsync(request, Token);
    }
    public async ValueTask DisposeAsync()
    {
        await api.DisposeAsync();
        await origin.DisposeAsync();
    }
}
internal sealed class CapturedSubscriptionSender : ITransactionalEmailSender
{
    public bool IsAvailable { get; set; } = true;
    public string PublicOrigin => "https://notification-qa.example.test";
    public ConcurrentQueue<TransactionalEmail> Messages { get; } = new();
    public ConcurrentQueue<Guid> AttemptedIds { get; } = new();
    public bool Fail { get; set; }
    public Func<TransactionalEmail, CancellationToken, Task>? BeforeSend { get; set; }
    public async Task SendAsync(TransactionalEmail email, CancellationToken token)
    {
        AttemptedIds.Enqueue(email.MessageId);
        if (BeforeSend is not null) await BeforeSend(email, token);
        token.ThrowIfCancellationRequested();
        if (Fail) throw new InvalidOperationException("Synthetic transactional SMTP outage");
        Messages.Enqueue(email);
    }
}
