using Acropolis.Catalog.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acropolis.Catalog.Infrastructure;

public static class CatalogRegistration
{
    public static IServiceCollection AddChannelCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<CatalogDbContext>(options => ConfigureDatabase(options, configuration.GetConnectionString("Database")!));
        services.AddScoped<ICatalogService, CatalogService>();
        return services;
    }
    public static void ConfigureDatabase(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, provider => provider.MigrationsHistoryTable(CatalogDbContext.HistoryTable, CatalogDbContext.Schema).CommandTimeout(3));
}
