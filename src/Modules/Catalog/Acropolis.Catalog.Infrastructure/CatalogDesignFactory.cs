using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Acropolis.Catalog.Infrastructure;

public sealed class CatalogDesignFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>();
        CatalogRegistration.ConfigureDatabase(options, "Host=localhost;Database=acropolis_test_design;Username=acropolis_migrator");
        return new(options.Options);
    }
}
