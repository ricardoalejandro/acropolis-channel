using Acropolis.Catalog.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acropolis.Catalog.Infrastructure;

public sealed class ConsumptionRetentionWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ConsumptionRetentionWorker> logger, IHostEnvironment environment, IOptions<ConsumptionRecordingOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // HTTP tests often replace the database services. They must opt in explicitly.
        if (environment.IsEnvironment("Testing") && !options.Value.EnableRetentionInTesting) return;
        // A restart schedules cleanup independently of whether new recording is enabled.
        while (!stoppingToken.IsCancellationRequested)
        {
            using var cycleDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cycleDeadline.Token);
            try
            {
                for (var batch = 0; batch < 10 && !budget.IsCancellationRequested; batch++)
                {
                    using var scope = scopes.CreateScope();
                    var result = await scope.ServiceProvider.GetRequiredService<IConsumptionRetentionService>().PruneAsync(budget.Token);
                    if (result.PulsesDeleted + result.SessionsDeleted + result.AccountRowsDeleted + result.GeneralRowsDeleted == 0) break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { logger.LogWarning("Consumption retention cycle reached its bounded budget."); }
            catch (Exception error) { logger.LogWarning("Consumption retention cycle failed ({FailureType}).", error.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
