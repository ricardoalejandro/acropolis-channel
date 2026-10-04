using System.Net;
using System.Net.Http.Json;
using Acropolis.Catalog.Application;
using Acropolis.Catalog.Infrastructure;
using Acropolis.Migrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Acropolis.Catalog.IntegrationTests;

[Collection("CatalogPostgres")]
public sealed class CatalogTests(CatalogFixture database)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private static CreateContentRequest Draft(string slug = "sabiduria-viva") => new(slug, "Sabiduría viva", "", "", "lecturas");
    private static UpdateContentRequest Change(AdminContentView item, string status, string? slug = null) => new(item.Version, status, slug ?? item.Slug, item.Title, "Resumen editorial", "Sinopsis editorial pública; no obra completa.", item.Category, "editorial-reading", 300);

    [Fact]
    public async Task PublicationWithdrawalArchiveAndRepublishPreserveStableLinksAndAudit()
    {
        await database.ResetAsync(Token);
        var clock = new CatalogClock();
        await using var context = database.Context();
        var service = new CatalogService(context, clock);
        Assert.Equal("validation_error", (await service.CreateAsync(Guid.NewGuid(), Draft() with { Title = "" }, Token)).Error);
        var item = (await service.CreateAsync(Guid.NewGuid(), Draft(), Token)).Value!;
        Assert.Equal("draft", item.Status); Assert.Null(item.PublishedUtc);
        Assert.Null(await service.GetPublishedAsync(item.Slug, Token));
        Assert.Equal(0, (await service.ListPublishedAsync(null, null, 1, 20, Token)).Total);
        Assert.Equal("validation_error", (await service.UpdateAsync(Guid.NewGuid(), item.Id, Change(item, "published") with { Body = "" }, Token)).Error);
        Assert.Equal("invalid_transition", (await service.UpdateAsync(Guid.NewGuid(), item.Id, Change(item, "archived"), Token)).Error);
        Assert.Equal("not_found", (await service.UpdateAsync(Guid.NewGuid(), Guid.NewGuid(), Change(item, "published"), Token)).Error);
        Assert.Null(await service.GetAdminAsync(Guid.NewGuid(), Token));
        clock.Advance();
        item = (await service.UpdateAsync(Guid.NewGuid(), item.Id, Change(item, "published"), Token)).Value!;
        var published = item.PublishedUtc;
        Assert.NotNull(published);
        Assert.Equal(item.Body, (await service.GetPublishedAsync(item.Slug, Token))!.Body);
        Assert.Equal(1, (await service.ListPublishedAsync(null, null, 1, 20, Token)).Total);
        Assert.Equal("slug_immutable", (await service.UpdateAsync(Guid.NewGuid(), item.Id, Change(item, "published", "nuevo-enlace"), Token)).Error);
        Assert.Equal("concurrency_conflict", (await service.UpdateAsync(Guid.NewGuid(), item.Id, Change(item, "published") with { Version = new string('a', 32) }, Token)).Error);
        clock.Advance();
        item = (await service.UpdateAsync(Guid.NewGuid(), item.Id, Change(item, "archived"), Token)).Value!;
        Assert.Null(await service.GetPublishedAsync(item.Slug, Token));
        Assert.Equal("invalid_transition", (await service.UpdateAsync(Guid.NewGuid(), item.Id, Change(item, "published"), Token)).Error);
        item = (await service.UpdateAsync(Guid.NewGuid(), item.Id, Change(item, "draft"), Token)).Value!;
        Assert.Equal(published, item.PublishedUtc);
        clock.Advance();
        item = (await service.UpdateAsync(Guid.NewGuid(), item.Id, Change(item, "published"), Token)).Value!;
        Assert.Equal(published, item.PublishedUtc);
        clock.Advance();
        item = (await service.UpdateAsync(Guid.NewGuid(), item.Id, Change(item, "draft"), Token)).Value!;
        Assert.Null(await service.GetPublishedAsync(item.Slug, Token));
        Assert.Equal("draft", (await service.GetAdminAsync(item.Id, Token))!.Status);
        Assert.Equal(6, await context.Audit.CountAsync(Token));
        Assert.All(await context.Audit.ToArrayAsync(Token), x => { Assert.NotEqual(Guid.Empty, x.ActorId); Assert.DoesNotContain("Sinopsis editorial", x.Changes); });
    }

    [Fact]
    public async Task SearchEscapesWildcardsAndPaginationReturnsOnlyPublishedMetadata()
    {
        await database.ResetAsync(Token);
        await using var context = database.Context();
        var clock = new CatalogClock();
        var data = new[]
        {
            ("tema-uno", "Filosofía 50%_\\", "cursos", "published"),
            ("tema-dos", "FILOSOFÍA ordinaria", "lecturas", "published"),
            ("tema-tres", "Otro tema", "cursos", "published"),
            ("tema-cuatro", "Filosofía privada", "cursos", "draft"),
            ("tema-cinco", "Filosofía archivada", "cursos", "archived")
        };
        foreach (var (slug,title,category,status) in data)
            context.Contents.Add(new EditorialContent { Id = Guid.NewGuid(), Slug = slug, Title = title, Summary = slug == "tema-tres" ? "Resumen FILOSOFÍA" : "Resumen", Body = "Sinopsis", Category = category, Status = status, CreatedUtc = clock.GetUtcNow(), UpdatedUtc = clock.GetUtcNow(), PublishedUtc = status == "draft" ? null : clock.GetUtcNow() });
        await context.SaveChangesAsync(Token);
        var service = new CatalogService(context, clock);
        Assert.Equal(3, (await service.ListPublishedAsync("filosofía", null, 1, 20, Token)).Total);
        foreach (var search in new[] { "%", "_", "\\", "50%_\\" })
            Assert.Equal("tema-uno", Assert.Single((await service.ListPublishedAsync(search, "cursos", 1, 20, Token)).Items).Slug);
        Assert.Empty((await service.ListPublishedAsync("no existe", null, 1, 20, Token)).Items);
        Assert.Equal(2, (await service.ListPublishedAsync("FILOSOFÍA", "cursos", 1, 20, Token)).Total);
        var first = await service.ListPublishedAsync(" ", null, 1, 1, Token);
        var second = await service.ListPublishedAsync(null, null, 2, 1, Token);
        Assert.Equal(3, first.Total); Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
        Assert.Empty((await service.ListPublishedAsync(null, null, 100, 20, Token)).Items);
        Assert.Equal(5, (await service.ListAdminAsync(null, null, null, 1, 20, Token)).Total);
        Assert.Single((await service.ListAdminAsync("filosofía", "cursos", "draft", 1, 20, Token)).Items);
        Assert.Equal(1, (await service.ListAdminAsync(null, null, "archived", 1, 20, Token)).Total);
    }

    [Fact]
    public async Task DuplicateSlugsAndConcurrentEditorsHaveOneWinnerAndNoOrphanAudit()
    {
        await database.ResetAsync(Token);
        Guid id; AdminContentView initial;
        await using (var context = database.Context())
        {
            var service = new CatalogService(context, TimeProvider.System);
            initial = (await service.CreateAsync(Guid.NewGuid(), Draft(), Token)).Value!; id = initial.Id;
            Assert.Equal("slug_conflict", (await service.CreateAsync(Guid.NewGuid(), Draft(), Token)).Error);
            context.ChangeTracker.Clear();
            var other = (await service.CreateAsync(Guid.NewGuid(), Draft("otro-enlace"), Token)).Value!;
            Assert.Equal("slug_conflict", (await service.UpdateAsync(Guid.NewGuid(), other.Id, Change(other, "draft", initial.Slug), Token)).Error);
        }
        var barrier = new SaveBarrier();
        async Task<CatalogResult<AdminContentView>> Edit(string title)
        {
            await using var context = database.Context(interceptor: barrier);
            return await new CatalogService(context, TimeProvider.System).UpdateAsync(Guid.NewGuid(), id, Change(initial, "published") with { Title = title }, Token);
        }
        var outcomes = await Task.WhenAll(Edit("Edición primera"), Edit("Edición segunda"));
        Assert.Single(outcomes, x => x.Succeeded); Assert.Single(outcomes, x => x.Error == "concurrency_conflict");
        await using var final = database.Context();
        Assert.Equal(3, await final.Audit.CountAsync(Token));
        Assert.Equal(2, await final.Contents.CountAsync(Token));
    }

    [Fact]
    public async Task UnexpectedDatabaseConstraintFailuresAreNotConvertedToSlugConflicts()
    {
        await database.ResetAsync(Token);
        await database.ExecuteAsync("ALTER TABLE catalog.\"Contents\" ADD CONSTRAINT synthetic_rejection CHECK (\"Title\" <> 'Rejected title')", Token);
        await using var context = database.Context();
        await Assert.ThrowsAsync<DbUpdateException>(() => new CatalogService(context, TimeProvider.System).CreateAsync(Guid.NewGuid(), Draft() with { Title = "Rejected title" }, Token));
        context.ChangeTracker.Clear();
        Assert.Empty(await context.Audit.ToArrayAsync(Token));
        var item = (await new CatalogService(context, TimeProvider.System).CreateAsync(Guid.NewGuid(), Draft(), Token)).Value!;
        await Assert.ThrowsAsync<DbUpdateException>(() => new CatalogService(context, TimeProvider.System).UpdateAsync(Guid.NewGuid(), item.Id, Change(item, "draft") with { Title = "Rejected title" }, Token));
        context.ChangeTracker.Clear();
        Assert.Equal(1, await context.Audit.CountAsync(Token));
    }

    [Fact]
    public async Task CatalogMigrationIsRepeatableAndReadinessRejectsMissingOrForeignHistory()
    {
        await database.ResetAsync(Token);
        await Task.WhenAll(new ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token), new ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token));
        await using var context = database.Context(true);
        Assert.False(context.Database.HasPendingModelChanges());
        using var design = new CatalogDesignFactory().CreateDbContext([]);
        Assert.False(design.Database.HasPendingModelChanges());
        await using var api = new CatalogApiFactory(database);
        using var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", Token)).StatusCode);
        await database.ExecuteAsync("INSERT INTO catalog.\"__EFMigrationsHistory\" VALUES ('20990101000000_Unknown','10.0.12')", Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready", Token)).StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token));
        await database.ExecuteAsync("DROP SCHEMA catalog CASCADE", Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Token)).StatusCode);
        await new ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token);
        foreach (var sql in new[] { "CREATE TABLE catalog.forbidden(id int)", "INSERT INTO catalog.\"__EFMigrationsHistory\" VALUES ('Forbidden','10')", "DELETE FROM catalog.\"Audit\"", "UPDATE catalog.\"Audit\" SET \"Action\"='forbidden'", "DELETE FROM catalog.\"Contents\"" })
        {
            await using var runtime = new NpgsqlConnection(database.RuntimeConnection); await runtime.OpenAsync(Token);
            await using var command = new NpgsqlCommand(sql, runtime);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Token))).SqlState);
        }
        await database.ExecuteAsync("DROP SCHEMA catalog CASCADE; CREATE SCHEMA catalog AUTHORIZATION acropolis_app", Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token));
        await database.ExecuteAsync("DROP SCHEMA catalog CASCADE; CREATE SCHEMA catalog AUTHORIZATION acropolis_migrator; CREATE TABLE catalog.sentinel(id int)", Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token));
    }

    [Fact]
    public async Task BulkQaSeedIsGuardedIdempotentAndPreservesEditedFixtures()
    {
        await database.ResetAsync(Token);
        await using var context = database.Context(true); context.Database.SetCommandTimeout(30);
        var operations = new CatalogOperations(context);
        await operations.SeedQaAsync(10000, Token);
        Assert.Equal(10000, await context.Contents.CountAsync(Token));
        Assert.Equal(8000, await context.Contents.CountAsync(x => x.Status == "published", Token));
        Assert.Equal(1000, await context.Contents.CountAsync(x => x.Status == "draft", Token));
        Assert.Equal(1000, await context.Contents.CountAsync(x => x.Status == "archived", Token));
        Assert.Equal(6, await context.Contents.Select(x => x.Category).Distinct().CountAsync(Token));
        await context.Contents.Where(x => x.Slug == "qa-catalog-000000").ExecuteUpdateAsync(x => x.SetProperty(c => c.Title, "Fixture edited"), Token);
        await operations.SeedQaAsync(10000, Token);
        Assert.Equal(10000, await context.Contents.CountAsync(Token));
        Assert.Equal("Fixture edited", (await context.Contents.SingleAsync(x => x.Slug == "qa-catalog-000000", Token)).Title);
        foreach (var invalid in new[] { 0, 100001 }) await Assert.ThrowsAsync<InvalidOperationException>(() => operations.SeedQaAsync(invalid, Token));
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        try { Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production"); await Assert.ThrowsAsync<InvalidOperationException>(() => operations.SeedQaAsync(1, Token)); }
        finally { Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", environment); }
        var options = new DbContextOptionsBuilder<CatalogDbContext>();
        CatalogRegistration.ConfigureDatabase(options, new NpgsqlConnectionStringBuilder(database.MigrationConnection) { Database = "postgres" }.ConnectionString);
        await using var invalidDatabase = new CatalogDbContext(options.Options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CatalogOperations(invalidDatabase).SeedQaAsync(1, Token));
    }

    private sealed class SaveBarrier : SaveChangesInterceptor
    {
        private int participants;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref participants) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }
}
public sealed class CatalogApiFactory(CatalogFixture fixture) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = fixture.RuntimeConnection, ["Identity:EmailEnabled"] = "false" }));
    }
}
