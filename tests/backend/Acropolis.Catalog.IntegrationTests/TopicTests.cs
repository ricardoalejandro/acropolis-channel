using Acropolis.Catalog.Application;
using Acropolis.Catalog.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Acropolis.Catalog.IntegrationTests;

[Collection("CatalogPostgres")]
public sealed class TopicTests(CatalogFixture database)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly Guid Actor = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static TopicService Service(CatalogDbContext context) => new(context, TimeProvider.System);
    private static EditorialContent Content(string slug = "qa-tema-obra", string state = "published") => new()
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        Title = "Lectura sintética",
        Summary = "Resumen literal 50%_\\",
        Body = "Sinopsis pública",
        Category = "lecturas",
        Tags = ["etiqueta libre"],
        WorkText = "Obra privada preservada",
        Status = state,
        CreatedUtc = DateTimeOffset.UtcNow,
        UpdatedUtc = DateTimeOffset.UtcNow,
        PublishedUtc = state == "published" ? DateTimeOffset.UtcNow : null
    };
    [Fact]
    public async Task MigrationStartsEmptyIsIdempotentAndRuntimeCannotDeleteTopicsOrRewriteAudit()
    {
        await database.ResetAsync(Token);
        await using var context = database.Context();
        Assert.Contains("20261007040000_AddTopics", context.Database.GetMigrations());
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Empty((await Service(context).ListPublicAsync(1, 20, Token)).Items);
        Assert.Equal(0, await context.Topics.CountAsync(Token));
        Assert.Single(await context.TopicDirectory.ToArrayAsync(Token));
        await new Acropolis.Migrations.ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token);
        var topic = (await Service(context).CreateAsync(Actor, new("tema-qa", "Tema QA"), Token)).Value!;
        foreach (var statement in new[] { "DELETE FROM catalog.\"Topics\"", "DELETE FROM catalog.\"TopicDirectory\"", "UPDATE catalog.\"TopicAudit\" SET \"Action\"='changed'", "DELETE FROM catalog.\"TopicAudit\"" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(statement, Token));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        Assert.Equal(topic.Id, (await Service(context).GetAdminAsync(topic.Id, Token))!.Id);
    }
    [Fact]
    public async Task LifecycleStableSlugNoOpsAndDuplicateArchivedSlugPreserveAudit()
    {
        await database.ResetAsync(Token); await using var context = database.Context(); var service = Service(context);
        var created = (await service.CreateAsync(Actor, new("ideas-qa", "  Ideas QA  "), Token)).Value!; Assert.Equal("Ideas QA", created.Name);
        var same = (await service.UpdateAsync(Actor, created.Id, new(created.Version, created.Name), Token)).Value!;
        Assert.Equal(created.Version, same.Version); Assert.Equal(1, await context.TopicAudit.CountAsync(Token));
        var renamed = (await service.UpdateAsync(Actor, created.Id, new(created.Version, "Otras ideas QA"), Token)).Value!;
        Assert.Equal(created.Slug, renamed.Slug); Assert.NotEqual(created.Version, renamed.Version);
        Assert.Equal("concurrency_conflict", (await service.UpdateAsync(Actor, created.Id, new(created.Version, "No sobrescribir"), Token)).Error);
        var archived = (await service.SetStateAsync(Actor, created.Id, new(renamed.Version, "archived"), Token)).Value!;
        Assert.Empty((await service.ListPublicAsync(1, 20, Token)).Items);
        Assert.Equal("topic_slug_conflict", (await service.CreateAsync(Actor, new(created.Slug, "Duplicado"), Token)).Error);
        var restored = (await service.SetStateAsync(Actor, created.Id, new(archived.Version, "active"), Token)).Value!;
        Assert.Equal(created.Id, restored.Id); Assert.Equal(created.Slug, restored.Slug);
        Assert.Equal(4, (await service.ListAuditAsync(created.Id, 1, 20, Token))!.Total);
        Assert.Null(await service.ListAuditAsync(Guid.NewGuid(), 1, 20, Token));
        Assert.Equal("not_found", (await service.SetStateAsync(Actor, Guid.NewGuid(), new(restored.Version, "active"), Token)).Error);
    }
    [Fact]
    public async Task OrderingUsesDirectoryCasAndOnlyMovesTheAffectedInterval()
    {
        await database.ResetAsync(Token); await using var context = database.Context(); var service = Service(context);
        var first = (await service.CreateAsync(Actor, new("uno-qa", "Uno"), Token)).Value!;
        var second = (await service.CreateAsync(Actor, new("dos-qa", "Dos"), Token)).Value!;
        var third = (await service.CreateAsync(Actor, new("tres-qa", "Tres"), Token)).Value!;
        var before = await service.ListAdminAsync(null, null, 1, 20, Token);
        Assert.True((await service.MoveAsync(Actor, new(before.DirectoryVersion, third.Id, first.Id), Token)).Succeeded);
        var after = await service.ListAdminAsync(null, null, 1, 20, Token);
        Assert.Equal(new[] { third.Id, first.Id, second.Id }, after.Items.Select(x => x.Id));
        Assert.Equal(new[] { 0, 1, 2 }, after.Items.Select(x => x.Position));
        Assert.Equal("concurrency_conflict", (await service.MoveAsync(Actor, new(before.DirectoryVersion, second.Id, first.Id), Token)).Error);
        Assert.True((await service.MoveAsync(Actor, new(after.DirectoryVersion, third.Id, null), Token)).Succeeded);
        var final = await service.ListAdminAsync(null, null, 1, 20, Token);
        Assert.Equal(new[] { first.Id, second.Id, third.Id }, final.Items.Select(x => x.Id));
        var noOp = await service.MoveAsync(Actor, new(final.DirectoryVersion, third.Id, null), Token);
        Assert.True(noOp.Succeeded); Assert.Equal(final.DirectoryVersion, (await service.ListAdminAsync(null, null, 1, 20, Token)).DirectoryVersion);
    }
    [Fact]
    public async Task AssignmentRotatesContentVersionPreservesMetadataAndArchivedLinksRequireExplicitRemoval()
    {
        await database.ResetAsync(Token); await using var context = database.Context(); var content = Content(); context.Contents.Add(content); await context.SaveChangesAsync(Token);
        var service = Service(context); var topic = (await service.CreateAsync(Actor, new("filosofia-qa", "Filosofía QA"), Token)).Value!;
        var originalVersion = content.Version;
        var assigned = (await service.AssignAsync(Actor, content.Id, new(content.Version, [topic.Id]), Token)).Value!;
        Assert.NotEqual(originalVersion, assigned.ContentVersion); Assert.Equal(assigned.ContentVersion, content.Version);
        Assert.Single(assigned.Items); Assert.Equal("Obra privada preservada", content.WorkText); Assert.Equal(new[] { "etiqueta libre" }, content.Tags); Assert.Equal("lecturas", content.Category);
        var version = assigned.ContentVersion; var auditCount = await context.Audit.CountAsync(Token);
        Assert.Equal(version, (await service.AssignAsync(Actor, content.Id, new(version, [topic.Id]), Token)).Value!.ContentVersion);
        Assert.Equal(auditCount, await context.Audit.CountAsync(Token));
        await service.SetStateAsync(Actor, topic.Id, new(topic.Version, "archived"), Token);
        Assert.Single((await service.GetContentTopicsAsync(content.Id, Token))!.Items);
        Assert.Empty((await service.ListPublishedByTopicAsync(topic.Slug, null, null, 1, 20, Token)).Items);
        Assert.Single((await new CatalogService(context, TimeProvider.System).ListPublishedAsync(null, null, 1, 20, Token)).Items);
        Assert.True((await service.AssignAsync(Actor, content.Id, new(version, [topic.Id]), Token)).Succeeded);
        var secondContent = Content("otra-obra-qa"); context.Contents.Add(secondContent); await context.SaveChangesAsync(Token);
        Assert.Equal("topic_unavailable", (await service.AssignAsync(Actor, secondContent.Id, new(secondContent.Version, [topic.Id]), Token)).Error);
        Assert.True((await service.AssignAsync(Actor, content.Id, new(version, []), Token)).Succeeded);
        Assert.Empty((await service.GetContentTopicsAsync(content.Id, Token))!.Items);
    }
    [Fact]
    public async Task PublicFilterCombinesLiteralSearchFormatAndTopicAndNeverExposesPrivateFields()
    {
        await database.ResetAsync(Token); await using var context = database.Context(); var service = Service(context);
        var topic = (await service.CreateAsync(Actor, new("busqueda-qa", "50%_\\"), Token)).Value!;
        foreach (var state in new[] { "published", "draft", "archived" })
        {
            var content = Content("obra-" + state, state); context.Contents.Add(content); await context.SaveChangesAsync(Token); await service.AssignAsync(Actor, content.Id, new(content.Version, [topic.Id]), Token);
        }
        Assert.Equal(1, (await service.ListPublishedByTopicAsync(topic.Slug, "50%_\\", "lecturas", 1, 20, Token)).Total);
        Assert.Empty((await service.ListPublishedByTopicAsync(topic.Slug, null, "videos", 1, 20, Token)).Items);
        Assert.Empty((await service.ListPublishedByTopicAsync("tema-inexistente", null, null, 1, 20, Token)).Items);
        Assert.Single((await service.ListAdminAsync("%_\\", "active", 1, 20, Token)).Items);
        Assert.Empty((await service.ListAdminAsync("inexistente", null, 1, 20, Token)).Items);
        var serialized = System.Text.Json.JsonSerializer.Serialize((await service.ListPublishedByTopicAsync(topic.Slug, null, null, 1, 20, Token)).Items);
        Assert.DoesNotContain("WorkText", serialized); Assert.DoesNotContain("Version", serialized); Assert.DoesNotContain("Obra privada", serialized);
    }
    [Fact]
    public async Task TwoAssignmentsWithOneContentVersionCannotOverwriteEachOther()
    {
        await database.ResetAsync(Token); Guid id; string version; Guid[] topics;
        await using (var setup = database.Context())
        {
            var content = Content(); setup.Contents.Add(content); await setup.SaveChangesAsync(Token); id = content.Id; version = content.Version;
            var service = Service(setup); topics = [(await service.CreateAsync(Actor, new("tema-uno", "Tema uno"), Token)).Value!.Id, (await service.CreateAsync(Actor, new("tema-dos", "Tema dos"), Token)).Value!.Id];
        }
        async Task<CatalogResult<ContentTopicsView>> Assign(Guid topic) { await using var scope = database.Context(); await scope.Database.OpenConnectionAsync(Token); return await Service(scope).AssignAsync(Actor, id, new(version, [topic]), Token); }
        var results = await Task.WhenAll(Assign(topics[0]), Assign(topics[1]));
        Assert.Single(results, x => x.Succeeded); Assert.Equal("concurrency_conflict", Assert.Single(results, x => !x.Succeeded).Error);
        await using var verify = database.Context(); Assert.Single((await Service(verify).GetContentTopicsAsync(id, Token))!.Items); Assert.Equal(1, await verify.Audit.CountAsync(Token));
    }
    [Fact]
    public async Task ConcurrentArchiveAndNewAssignmentSerializeWithoutDeletingContent()
    {
        await database.ResetAsync(Token); Guid id; string contentVersion; AdminTopicView topic;
        await using (var setup = database.Context())
        {
            var content = Content(); setup.Contents.Add(content); await setup.SaveChangesAsync(Token); id = content.Id; contentVersion = content.Version;
            topic = (await Service(setup).CreateAsync(Actor, new("tema-race", "Tema concurrente"), Token)).Value!;
        }
        await using var assignScope = database.Context(); await using var archiveScope = database.Context();
        await assignScope.Database.OpenConnectionAsync(Token); await archiveScope.Database.OpenConnectionAsync(Token);
        var assignTask = Service(assignScope).AssignAsync(Actor, id, new(contentVersion, [topic.Id]), Token);
        var archiveTask = Service(archiveScope).SetStateAsync(Actor, topic.Id, new(topic.Version, "archived"), Token);
        await Task.WhenAll(assignTask, archiveTask);
        var assigned = await assignTask;
        var archived = await archiveTask;
        Assert.True(archived.Succeeded); Assert.True(assigned.Succeeded || assigned.Error == "topic_unavailable");
        await using var verify = database.Context(); Assert.Equal("archived", (await Service(verify).GetAdminAsync(topic.Id, Token))!.Status);
        Assert.Equal(assigned.Succeeded ? 1 : 0, await verify.ContentTopics.CountAsync(Token)); Assert.Equal("published", (await verify.Contents.SingleAsync(Token)).Status);
    }
    [Fact]
    public async Task FailureAfterSqlRollsBackAssociationsContentVersionAndAuditTogether()
    {
        await database.ResetAsync(Token); EditorialContent content; AdminTopicView topic;
        await using (var setup = database.Context()) { content = Content(); setup.Contents.Add(content); await setup.SaveChangesAsync(Token); topic = (await Service(setup).CreateAsync(Actor, new("rollback-qa", "Rollback QA"), Token)).Value!; }
        await using var failing = database.Context(interceptor: new ThrowAfterSave());
        var service = Service(failing);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AssignAsync(Actor, content.Id, new(content.Version, [topic.Id]), Token));
        Assert.Empty(failing.ChangeTracker.Entries());
        await using (var verify = database.Context()) { Assert.Empty(await verify.ContentTopics.ToArrayAsync(Token)); Assert.Empty(await verify.Audit.ToArrayAsync(Token)); Assert.Equal(content.Version, (await verify.Contents.SingleAsync(Token)).Version); }
        var retried = await service.AssignAsync(Actor, content.Id, new(content.Version, [topic.Id]), Token);
        Assert.True(retried.Succeeded); Assert.NotEqual(content.Version, retried.Value!.ContentVersion);
        Assert.Equal(1, await failing.ContentTopics.CountAsync(Token)); Assert.Equal(1, await failing.Audit.CountAsync(Token));
    }
    [Fact]
    public async Task DatabaseConstraintRejectsThirteenLinksAndCancellationWritesNothing()
    {
        await database.ResetAsync(Token); await using var context = database.Context(); var content = Content(); context.Contents.Add(content); await context.SaveChangesAsync(Token);
        var service = Service(context); var ids = new List<Guid>();
        for (var index = 0; index < 13; index++) ids.Add((await service.CreateAsync(Actor, new("tema-" + index, "Tema " + index), Token)).Value!.Id);
        Assert.Equal("validation_error", (await service.AssignAsync(Actor, content.Id, new(content.Version, ids.ToArray()), Token)).Error);
        await using (var raw = database.Context())
        {
            raw.ContentTopics.AddRange(ids.Select(id => new ContentTopic { ContentId = content.Id, TopicId = id }));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => raw.SaveChangesAsync(Token));
            Assert.Equal("CK_ContentTopics_Limit", Assert.IsType<PostgresException>(error.InnerException).ConstraintName);
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AssignAsync(Actor, content.Id, new(content.Version, [ids[0]]), cancelled.Token));
        Assert.Empty((await service.GetContentTopicsAsync(content.Id, Token))!.Items);
        Assert.True((await service.AssignAsync(Actor, content.Id, new(content.Version, [ids[0]]), Token)).Succeeded);
    }
    [Fact]
    public async Task ConcurrentMovesWithOneDirectoryVersionHaveExactlyOneCommit()
    {
        await database.ResetAsync(Token); AdminTopicView first; AdminTopicView second; string version;
        await using (var setup = database.Context())
        {
            var service = Service(setup); first = (await service.CreateAsync(Actor, new("orden-uno", "Orden uno"), Token)).Value!;
            second = (await service.CreateAsync(Actor, new("orden-dos", "Orden dos"), Token)).Value!;
            await service.CreateAsync(Actor, new("orden-tres", "Orden tres"), Token); version = (await service.ListAdminAsync(null, null, 1, 20, Token)).DirectoryVersion;
        }
        await using var left = database.Context(); await using var right = database.Context(); await left.Database.OpenConnectionAsync(Token); await right.Database.OpenConnectionAsync(Token);
        var results = await Task.WhenAll(Service(left).MoveAsync(Actor, new(version, first.Id, null), Token), Service(right).MoveAsync(Actor, new(version, second.Id, first.Id), Token));
        Assert.Single(results, result => result.Succeeded); Assert.Equal("concurrency_conflict", Assert.Single(results, result => !result.Succeeded).Error);
        await using var verify = database.Context(); var directory = await Service(verify).ListAdminAsync(null, null, 1, 20, Token);
        Assert.Equal(new[] { 0, 1, 2 }, directory.Items.Select(item => item.Position)); Assert.Equal(3, directory.Items.Select(item => item.Id).Distinct().Count()); Assert.Equal(1, await verify.TopicAudit.CountAsync(item => item.Action == "topic.moved", Token));
    }
    [Fact]
    public async Task RealCancellationAfterSavedSqlRollsBackAndAllowsReuseOfTheSameContext()
    {
        await database.ResetAsync(Token); EditorialContent content; AdminTopicView topic;
        await using (var setup = database.Context()) { content = Content(); setup.Contents.Add(content); await setup.SaveChangesAsync(Token); topic = (await Service(setup).CreateAsync(Actor, new("cancelar-qa", "Cancelar QA"), Token)).Value!; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token); await using var context = database.Context(interceptor: new CancelAfterSave(cancellation)); var service = Service(context);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AssignAsync(Actor, content.Id, new(content.Version, [topic.Id]), cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested); Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Empty(context.ChangeTracker.Entries());
        await using (var verify = database.Context()) { Assert.Empty(await verify.ContentTopics.ToArrayAsync(Token)); Assert.Empty(await verify.Audit.ToArrayAsync(Token)); Assert.Equal(content.Version, (await verify.Contents.SingleAsync(Token)).Version); }
        var retried = await service.AssignAsync(Actor, content.Id, new(content.Version, [topic.Id]), Token); Assert.True(retried.Succeeded); Assert.Single(retried.Value!.Items); Assert.Equal(1, await context.Audit.CountAsync(Token));
    }
    private sealed class CancelAfterSave(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        private bool cancelled;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!cancelled) { cancelled = true; cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class ThrowAfterSave : SaveChangesInterceptor
    {
        private bool failed;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!failed) { failed = true; throw new InvalidOperationException("synthetic post-SQL rollback"); }
            return ValueTask.FromResult(result);
        }
    }
}
