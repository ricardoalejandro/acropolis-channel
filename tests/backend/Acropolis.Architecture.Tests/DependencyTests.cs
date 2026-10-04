using System.Reflection;
using Acropolis.Platform.Application;
using Acropolis.Platform.Infrastructure;

namespace Acropolis.Architecture.Tests;

public sealed class DependencyTests
{
    [Fact]
    public void ApplicationLayerDoesNotDependOnHostsOrPersistence()
    {
        var dependencies = typeof(GreetingService).Assembly.GetReferencedAssemblies().Select(name => name.Name!).ToArray();
        Assert.DoesNotContain(dependencies, name =>
            name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || name.StartsWith("Npgsql", StringComparison.Ordinal)
            || name.Contains("Infrastructure", StringComparison.Ordinal)
            || name.StartsWith("Acropolis.Api", StringComparison.Ordinal)
            || name.StartsWith("Acropolis.Migrations", StringComparison.Ordinal));
    }

    [Fact]
    public void InfrastructureDoesNotReferenceTheApiHost()
    {
        var references = typeof(PlatformDbContext).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, name => name.Name == "Acropolis.Api");
        Assert.Contains(references, name => name.Name == "Acropolis.Platform.Application");
    }

    [Fact]
    public void MigrationHostDoesNotDependOnTheWebHost()
    {
        var references = Assembly.Load("Acropolis.Migrations").GetReferencedAssemblies();
        Assert.DoesNotContain(references, name => name.Name == "Acropolis.Api");
        Assert.Contains(references, name => name.Name == "Acropolis.Platform.Infrastructure");
    }
}
