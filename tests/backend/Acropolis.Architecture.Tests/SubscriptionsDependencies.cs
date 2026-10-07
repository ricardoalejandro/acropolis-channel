using Acropolis.Subscriptions.Application;
using Acropolis.Subscriptions.Infrastructure;

namespace Acropolis.Architecture.Tests;

public sealed class SubscriptionsDependencies
{
    [Fact]
    public void SubscriptionContractsRemainIndependentOfHostsAndPersistence()
    {
        var dependencies = typeof(ISubscriptionAccess).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        Assert.DoesNotContain(dependencies, name => name.Contains("EntityFramework", StringComparison.Ordinal)
            || name.StartsWith("Npgsql", StringComparison.Ordinal) || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || name.EndsWith(".Infrastructure", StringComparison.Ordinal) || name is "Acropolis.Api" or "Acropolis.Migrations");
    }

    [Fact]
    public void SubscriptionPersistenceOwnsOnlyItsModule()
    {
        var dependencies = typeof(SubscriptionsDbContext).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        Assert.Contains("Acropolis.Subscriptions.Application", dependencies);
        Assert.DoesNotContain(dependencies, name => name is "Acropolis.Api" or "Acropolis.Migrations"
            || (name.EndsWith(".Infrastructure", StringComparison.Ordinal) && name != "Acropolis.Subscriptions.Infrastructure"));
    }
}
