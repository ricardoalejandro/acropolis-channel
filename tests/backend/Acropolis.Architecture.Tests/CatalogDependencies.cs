using Acropolis.Catalog.Application;
using Acropolis.Catalog.Infrastructure;

namespace Acropolis.Architecture.Tests;

public sealed class CatalogDependencies
{
    [Fact]
    public void ApplicationKeepsRulesIndependentOfPersistenceAndHttp()
    {
        var dependencies = typeof(CatalogRules).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        Assert.DoesNotContain(dependencies, name => name.Contains("EntityFramework") || name.StartsWith("Npgsql") || name.StartsWith("Microsoft.AspNetCore") || name.EndsWith(".Infrastructure") || name is "Acropolis.Api" or "Acropolis.Migrations");
    }
    [Fact]
    public void CatalogOwnsPersistenceWithoutReadingAnotherModuleInfrastructure()
    {
        var dependencies = typeof(CatalogDbContext).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        Assert.Contains("Acropolis.Catalog.Application", dependencies);
        Assert.DoesNotContain(dependencies, name => name is "Acropolis.Identity.Infrastructure" or "Acropolis.Platform.Infrastructure" or "Acropolis.Api" or "Acropolis.Migrations");
    }
}
