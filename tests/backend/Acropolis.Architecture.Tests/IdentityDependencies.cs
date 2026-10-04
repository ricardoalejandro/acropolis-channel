using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;

namespace Acropolis.Architecture.Tests;

public sealed class IdentityDependencies
{
    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrTheWebHost()
    {
        var dependencies = typeof(IdentityRules).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        Assert.DoesNotContain(dependencies, name => name.Contains("EntityFramework") || name.StartsWith("Npgsql") || name.StartsWith("Microsoft.AspNetCore") || name.EndsWith(".Infrastructure") || name is "Acropolis.Api" or "Acropolis.Migrations");
    }
    [Fact]
    public void IdentityOwnsItsPersistenceAndNeverReferencesForeignInfrastructure()
    {
        var dependencies = typeof(IdentityDbContext).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        Assert.Contains("Acropolis.Identity.Application", dependencies);
        Assert.DoesNotContain("Acropolis.Platform.Infrastructure", dependencies);
        Assert.DoesNotContain("Acropolis.Api", dependencies);
        Assert.DoesNotContain("Acropolis.Migrations", dependencies);
    }
}
