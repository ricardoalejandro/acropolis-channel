using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acropolis.Platform.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acropolis.Api.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task GreetingUsesTheVersionedJsonContractAndSecurityHeaders()
    {
        await using var factory = new ApiFactory(true);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/greeting", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var greeting = await response.Content.ReadFromJsonAsync<Greeting>(TestContext.Current.CancellationToken);
        Assert.Equal("Hola mundo", greeting!.Message);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("frame-ancestors 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task LivenessDoesNotConsultDatabaseState()
    {
        await using var factory = new ApiFactory(null);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", (await response.Content.ReadFromJsonAsync<StatusResponse>(TestContext.Current.CancellationToken))!.Status);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.OK, "ok")]
    [InlineData(false, HttpStatusCode.ServiceUnavailable, "not_ready")]
    public async Task ReadinessUsesPublicSanitizedStatus(bool ready, HttpStatusCode statusCode, string status)
    {
        await using var factory = new ApiFactory(ready);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(statusCode, response.StatusCode);
        Assert.Equal(status, (await response.Content.ReadFromJsonAsync<StatusResponse>(TestContext.Current.CancellationToken))!.Status);
    }

    [Theory]
    [InlineData("/api/missing-route")]
    [InlineData("/health/missing-route")]
    [InlineData("/openapi/missing-route")]
    public async Task UnknownApiPathUsesProblemDetailsRatherThanTheSpa(string path)
    {
        await using var factory = new ApiFactory(true);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(404, problem.RootElement.GetProperty("status").GetInt32());
        Assert.True(problem.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task OpenApiIsNotPublishedOutsideDevelopment()
    {
        await using var factory = new ApiFactory(true);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OpenApiIsAvailableInDevelopment()
    {
        await using var factory = new ApiFactory(true, "Development");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/v1/greeting", out _));
    }

    [Theory]
    [InlineData(null, "Database configuration is required.")]
    [InlineData("InvalidKey=private-password", "Database configuration is invalid.")]
    public async Task InvalidConfigurationFailsAtStartupWithoutLeakingItsValue(string? connectionString, string message)
    {
        await using var factory = new ApiFactory(true, connectionString: connectionString);
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Equal(message, error.Message);
        Assert.DoesNotContain("private-password", error.ToString(), StringComparison.Ordinal);
    }

    private sealed record StatusResponse(string Status);

    private sealed class ApiFactory(bool? ready, string environment = "Testing", string? connectionString = "Host=127.0.0.1;Port=1;Database=acropolis_test_api;Username=unused") : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Database"] = connectionString
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPlatformStateReader>();
                services.AddSingleton<IPlatformStateReader>(new Reader(ready));
            });
        }
    }

    private sealed class Reader(bool? ready) : IPlatformStateReader
    {
        public Task<bool> IsCurrentAsync(CancellationToken cancellationToken) =>
            ready is bool current
                ? Task.FromResult(current)
                : throw new InvalidOperationException("Liveness must not call the database probe.");
    }
}
