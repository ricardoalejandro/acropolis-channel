using Npgsql;
using Acropolis.Catalog.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acropolis.Catalog.Infrastructure;

public static class CatalogRegistration
{
    public static IServiceCollection AddChannelCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<CatalogDbContext>((provider, options) => ConfigureDatabase(options, provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddScoped<ICatalogService, CatalogService>();
        return services;
    }
    public static void ConfigureDatabase(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, provider => provider.MigrationsHistoryTable(CatalogDbContext.HistoryTable, CatalogDbContext.Schema).CommandTimeout(3));
    public static void ConfigureDatabase(DbContextOptionsBuilder options, NpgsqlDataSource dataSource) =>
        options.UseNpgsql(dataSource, provider => provider.MigrationsHistoryTable(CatalogDbContext.HistoryTable, CatalogDbContext.Schema).CommandTimeout(3));
}
