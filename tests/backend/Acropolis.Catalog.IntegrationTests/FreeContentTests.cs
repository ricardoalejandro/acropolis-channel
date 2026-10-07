using System.Text.Json;
using Acropolis.Catalog.Application;
using Acropolis.Catalog.Infrastructure;
using Acropolis.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Acropolis.Catalog.IntegrationTests;

[Collection("CatalogPostgres")]
public sealed class FreeContentTests(CatalogFixture database)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private static UpdateContentRequest Change(AdminContentView row, bool? isFree = null) =>
        new(row.Version, "published", row.Slug, row.Title, row.Summary, row.Body, row.Category, WorkText: row.WorkText, IsFree: isFree);

    [Fact]
    public async Task MigrationDefaultsExistingAndOmittedFlagsToFalseAndReplaysWithoutModelDrift()
    {
        await database.ResetAsync(Token);
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using (var old = database.Context(true))
        {
            await old.GetService<IMigrator>().MigrateAsync("20261007043000_AddConsumptionActivity", Token);
            await old.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO catalog."Contents" ("Id","Slug","Title","Summary","Body","Category","Status","CreatedUtc","UpdatedUtc","Version")
                VALUES ({id},'legacy-free-default','Legado QA','Resumen','Sinopsis','lecturas','draft',{now},{now},{new string('a', 32)})
                """, Token);
        }
        await new ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token);
        await new ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token);
        await using var context = database.Context();
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.False((await context.Contents.AsNoTracking().SingleAsync(x => x.Id == id, Token)).IsFree);
        var service = new CatalogService(context, TimeProvider.System);
        var created = (await service.CreateAsync(Guid.NewGuid(), new("new-free-default", "Nueva obra", "Resumen", "Sinopsis", "lecturas"), Token)).Value!;
        Assert.False(created.IsFree);
        Assert.False((await context.Contents.AsNoTracking().SingleAsync(x => x.Id == created.Id, Token)).IsFree);
    }

    [Fact]
    public async Task MarkerIsExplicitAuditedAndPreservedWhenOmittedWithoutLeakingPrivateWork()
    {
        await database.ResetAsync(Token);
        await using var context = database.Context(); var service = new CatalogService(context, TimeProvider.System);
        var actor = Guid.NewGuid();
        var row = (await service.CreateAsync(actor, new("free-marked-reading", "Lectura QA", "Resumen público", "Sinopsis pública", "lecturas", WorkText: "PRIVATE FREE WORK"), Token)).Value!;
        Assert.False(row.IsFree);
        row = (await service.UpdateAsync(actor, row.Id, Change(row), Token)).Value!;
        var denied = await service.GetPublishedWorkAsync(row.Slug, false, Token);
        Assert.Equal("content_requires_plan", denied.Error); Assert.Equal(403, denied.Status); Assert.Null(denied.Value);
        Assert.Equal(row.WorkText, (await service.GetPublishedWorkAsync(row.Slug, true, Token)).Value!.WorkText);
        var oldVersion = row.Version;
        row = (await service.UpdateAsync(actor, row.Id, Change(row, true), Token)).Value!;
        Assert.True(row.IsFree); Assert.NotEqual(oldVersion, row.Version);
        var audit = (await service.ListAuditAsync(row.Id, 1, 20, Token))!;
        Assert.Contains(audit.Items, item => item.Changes.Contains("\"isFree\":true", StringComparison.Ordinal));
        Assert.All(audit.Items, item => Assert.DoesNotContain("PRIVATE FREE WORK", item.Changes));
        row = (await service.UpdateAsync(actor, row.Id, Change(row) with { Title = "Título actualizado" }, Token)).Value!;
        Assert.True(row.IsFree);
        Assert.True((await service.GetPublishedWorkAsync(row.Slug, false, Token)).Value!.IsFree);
        var detail = (await service.GetPublishedAsync(row.Slug, Token))!;
        Assert.True(detail.IsFree);
        Assert.True(Assert.Single((await service.ListPublishedAsync(null, null, 1, 20, Token)).Items).IsFree);
        Assert.True((await service.GetAdminSummaryAsync(row.Id, Token))!.IsFree);
        var json = JsonSerializer.SerializeToElement(detail, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(json.GetProperty("isFree").GetBoolean());
        Assert.False(json.TryGetProperty("workText", out _)); Assert.False(json.TryGetProperty("youTubeId", out _));
        Assert.DoesNotContain("PRIVATE FREE WORK", json.GetRawText());
        var topics = new TopicService(context, TimeProvider.System);
        var topic = (await topics.CreateAsync(actor, new("free-marker-topic", "Tema QA"), Token)).Value!;
        Assert.True((await topics.AssignAsync(actor, row.Id, new(row.Version, [topic.Id]), Token)).Succeeded);
        row = (await service.GetAdminAsync(row.Id, Token))!;
        var filtered = Assert.Single((await topics.ListPublishedByTopicAsync(topic.Slug, null, null, 1, 20, Token)).Items);
        Assert.True(filtered.IsFree);
        Assert.DoesNotContain("PRIVATE FREE WORK", JsonSerializer.Serialize(filtered));
        row = (await service.UpdateAsync(actor, row.Id, Change(row, false), Token)).Value!;
        Assert.False(Assert.Single((await topics.ListPublishedByTopicAsync(topic.Slug, null, null, 1, 20, Token)).Items).IsFree);
        Assert.Equal("content_requires_plan", (await service.GetPublishedWorkAsync(row.Slug, false, Token)).Error);
        Assert.Equal(row.WorkText, (await service.GetPublishedWorkAsync(row.Slug, true, Token)).Value!.WorkText);
    }

    [Fact]
    public async Task FailedMarkerUpdateRollsBackFlagVersionAndAuditTogether()
    {
        await database.ResetAsync(Token);
        AdminContentView row;
        await using (var setup = database.Context())
        {
            var service = new CatalogService(setup, TimeProvider.System);
            row = (await service.CreateAsync(Guid.NewGuid(), new("free-marker-rollback", "Lectura QA", "Resumen", "Sinopsis", "lecturas", WorkText: "PRIVATE ROLLBACK WORK"), Token)).Value!;
            row = (await service.UpdateAsync(Guid.NewGuid(), row.Id, Change(row), Token)).Value!;
        }
        const string remove = "DROP TRIGGER IF EXISTS qa_free_marker_failure ON catalog.\"Contents\"; DROP FUNCTION IF EXISTS catalog.qa_free_marker_failure();";
        try
        {
            await database.ExecuteAsync("""
                CREATE FUNCTION catalog.qa_free_marker_failure() RETURNS trigger LANGUAGE plpgsql AS $qa$
                BEGIN RAISE EXCEPTION 'Synthetic free marker failure' USING ERRCODE='23514'; END; $qa$;
                CREATE TRIGGER qa_free_marker_failure BEFORE UPDATE OF "IsFree" ON catalog."Contents"
                FOR EACH ROW WHEN (NEW."IsFree" IS DISTINCT FROM OLD."IsFree") EXECUTE FUNCTION catalog.qa_free_marker_failure();
                """, Token);
            await using (var editing = database.Context())
            {
                var error = await Assert.ThrowsAsync<DbUpdateException>(() => new CatalogService(editing, TimeProvider.System).UpdateAsync(Guid.NewGuid(), row.Id, Change(row, true), Token));
                Assert.Equal("23514", Assert.IsType<PostgresException>(error.InnerException).SqlState);
            }
            await using var verify = database.Context();
            var persisted = await verify.Contents.AsNoTracking().SingleAsync(x => x.Id == row.Id, Token);
            Assert.False(persisted.IsFree); Assert.Equal(row.Version, persisted.Version);
            Assert.Equal(2, await verify.Audit.CountAsync(x => x.ContentId == row.Id, Token));
        }
        finally { await database.ExecuteAsync(remove, CancellationToken.None); }
    }
}
