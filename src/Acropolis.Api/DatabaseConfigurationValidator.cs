using Acropolis.Platform.Infrastructure;

internal sealed class DatabaseConfigurationValidator(IConfiguration configuration) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("Database");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Database configuration is required.");
        }
        try
        {
            _ = PlatformDbContext.CreateOptions(connectionString);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Database configuration is invalid.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
