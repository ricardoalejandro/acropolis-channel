using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Acropolis.Subscriptions.Infrastructure;

public sealed class SubscriptionsDesignFactory : IDesignTimeDbContextFactory<SubscriptionsDbContext>
{
    public SubscriptionsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SubscriptionsDbContext>();
        SubscriptionsRegistration.ConfigureDatabase(options, "Host=localhost;Database=acropolis_design;Username=design");
        return new SubscriptionsDbContext(options.Options);
    }
}
