using Acropolis.Subscriptions.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acropolis.Subscriptions.Infrastructure;

public sealed class SubscriptionNotificationWorker(IServiceScopeFactory scopes, IOptions<SubscriptionNotificationOptions> settings,
    IHostEnvironment environment, TimeProvider clock, ILogger<SubscriptionNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.Enabled || environment.IsEnvironment("Testing") && !settings.Value.RunInTesting) return;
        using var timer = new PeriodicTimer(SubscriptionNotificationRules.SubmissionSpacing, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SubscriptionNotificationScheduler>().ScheduleAsync(deadline.Token);
                var dispatcher = scope.ServiceProvider.GetRequiredService<SubscriptionNotificationDispatcher>();
                for (var index = 0; index < SubscriptionNotificationRules.BatchSize && await dispatcher.DispatchAsync(deadline.Token); index++) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Subscription notifications are temporarily unavailable."); }
        }
    }
}
