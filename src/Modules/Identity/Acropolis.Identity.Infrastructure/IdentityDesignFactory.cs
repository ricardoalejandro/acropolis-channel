using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Acropolis.Identity.Infrastructure;

public sealed class IdentityDesignFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityRegistration.ConfigureDatabase(options, "Host=localhost;Database=acropolis_design;Username=design");
        return new(options.Options);
    }
}
