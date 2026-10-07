using Acropolis.Subscriptions.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Acropolis.Subscriptions.Infrastructure;

public static class SubscriptionsRegistration
{
    public static IServiceCollection AddChannelSubscriptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<SubscriptionsDbContext>((provider, options) => ConfigureDatabase(options, provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddScoped<SubscriptionService>();
        services.AddScoped<ISubscriptionReportService, SubscriptionReportService>();
        services.AddScoped<ISubscriptionEventReportService, SubscriptionReportService>();
        services.AddScoped<ISubscriptionService>(provider => provider.GetRequiredService<SubscriptionService>());
        services.AddScoped<ISubscriptionAccess>(provider => provider.GetRequiredService<SubscriptionService>());
        return services;
    }
    public static void ConfigureDatabase(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, provider => provider.MigrationsHistoryTable(SubscriptionsDbContext.HistoryTable, SubscriptionsDbContext.Schema).CommandTimeout(3));
    public static void ConfigureDatabase(DbContextOptionsBuilder options, NpgsqlDataSource dataSource) =>
        options.UseNpgsql(dataSource, provider => provider.MigrationsHistoryTable(SubscriptionsDbContext.HistoryTable, SubscriptionsDbContext.Schema).CommandTimeout(3));
}
