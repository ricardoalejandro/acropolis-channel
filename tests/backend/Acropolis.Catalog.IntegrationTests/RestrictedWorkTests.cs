using System.Text.Json;
using Acropolis.Catalog.Application;
using Acropolis.Catalog.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Acropolis.Catalog.IntegrationTests;

[Collection("CatalogPostgres")]
public sealed class RestrictedWorkTests(CatalogFixture database)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private static CreateContentRequest Reading(string slug) => new(slug, "Una lectura", "Resumen público", "Sinopsis pública", "lecturas", Author: "Autora editorial", Tags: ["Filosofía"], WorkText: "OBRA RESTRINGIDA. Dos < tres.\nTexto seguro.");
    private static UpdateContentRequest Change(AdminContentView x, string status) => new(x.Version, status, x.Slug, x.Title, x.Summary, x.Body, x.Category, x.CoverAsset, x.DurationSeconds, x.Author, x.Tags, x.WorkText, x.YouTubeId, x.CollectionKind, x.ItemIds);
    private async Task<AdminContentView> Publish(CatalogService service, CreateContentRequest request)
    {
        var draft = (await service.CreateAsync(Guid.NewGuid(), request, Token)).Value!;
        var published = await service.UpdateAsync(Guid.NewGuid(), draft.Id, Change(draft, "published"), Token);
        Assert.True(published.Succeeded);
        return published.Value!;
    }
    [Fact]
    public async Task RestrictedTextAndMediaAreSeparatedFromPublicMetadataAndAudit()
    {
        await database.ResetAsync(Token);
        await using var context = database.Context();
        var service = new CatalogService(context, TimeProvider.System);
        var reading = await Publish(service, Reading("lectura-protegida"));
        var video = await Publish(service, Reading("video-protegido") with { Category = "podcast", WorkText = null, YouTubeId = "dQw4w9WgXcQ" });
        Assert.Equal(reading.WorkText, (await service.GetPublishedWorkAsync(reading.Slug, Token))!.WorkText);
        var playback = (await service.GetPublishedWorkAsync(video.Slug, Token))!;
        Assert.Equal(video.YouTubeId, playback.YouTubeId); Assert.Null(playback.WorkText);
        var detail = (await service.GetPublishedAsync(reading.Slug, Token))!;
        Assert.Equal(reading.Body, detail.Body); Assert.Equal(reading.Author, detail.Author); Assert.Equal(reading.Tags, detail.Tags);
        var publicJson = JsonSerializer.Serialize(new { detail, list = await service.ListPublishedAsync(null, null, 1, 20, Token), video = await service.GetPublishedAsync(video.Slug, Token) });
        foreach (var secret in new[] { "OBRA RESTRINGIDA", "dQw4w9WgXcQ", "WorkText", "YouTubeId", "Version", "Status" }) Assert.DoesNotContain(secret, publicJson);
        var admin = (await service.GetAdminAsync(reading.Id, Token))!;
        Assert.Equal(reading.WorkText, admin.WorkText);
        var narrow = (await service.GetAdminSummaryAsync(reading.Id, Token))!;
        Assert.Equal(reading.Title, narrow.Title);
        Assert.Null(await service.GetAdminSummaryAsync(Guid.NewGuid(), Token));
        Assert.DoesNotContain("OBRA RESTRINGIDA", JsonSerializer.Serialize(narrow));
        Assert.All(await context.Audit.ToArrayAsync(Token), x => { Assert.DoesNotContain("OBRA RESTRINGIDA", x.Changes); Assert.DoesNotContain(video.YouTubeId!, x.Changes); Assert.DoesNotContain("Autora editorial", x.Changes); });
        var legacy = await Publish(service, Reading("ficha-sin-obra") with { WorkText = null });
        Assert.NotNull(await service.GetPublishedAsync(legacy.Slug, Token));
        Assert.Null(await service.GetPublishedWorkAsync(legacy.Slug, Token));
        Assert.Null(await service.GetPublishedWorkAsync("inexistente", Token));
        reading = (await service.UpdateAsync(Guid.NewGuid(), reading.Id, Change(reading, "draft"), Token)).Value!;
        Assert.Null(await service.GetPublishedWorkAsync(reading.Slug, Token));
        Assert.Null(await service.GetPublishedAsync(reading.Slug, Token));
    }
    [Fact]
    public async Task CoursesAndProgramsPreserveOrderAndRecheckCurrentPublicationAtEveryDepth()
    {
        await database.ResetAsync(Token);
        await using var context = database.Context();
        var service = new CatalogService(context, TimeProvider.System);
        var first = await Publish(service, Reading("primera-obra"));
        var second = await Publish(service, Reading("segunda-obra") with { Category = "videos", WorkText = null, YouTubeId = "a_B-0123456" });
        var courseRequest = Reading("curso-ordenado") with { Category = "cursos", WorkText = null, CollectionKind = "course", ItemIds = [second.Id, first.Id] };
        var course = await Publish(service, courseRequest);
        var otherCourse = await Publish(service, courseRequest with { Slug = "curso-alternativo", ItemIds = [first.Id] });
        var program = await Publish(service, courseRequest with { Slug = "programa-ordenado", CollectionKind = "program", ItemIds = [otherCourse.Id, course.Id] });
        Assert.Equal([second.Id, first.Id], (await service.GetPublishedWorkAsync(course.Slug, Token))!.Items.Select(x => x.Id));
        Assert.Equal([otherCourse.Id, course.Id], (await service.GetPublishedWorkAsync(program.Slug, Token))!.Items.Select(x => x.Id));
        Assert.Equal([second.Id, first.Id], (await service.GetPublishedAsync(course.Slug, Token))!.Items!.Select(x => x.Id));
        foreach (var bad in new[]
        {
            courseRequest with { Slug = "curso-inexistente", ItemIds = [Guid.NewGuid()] },
            courseRequest with { Slug = "curso-anidado", ItemIds = [course.Id] },
            courseRequest with { Slug = "programa-obra", CollectionKind = "program", ItemIds = [first.Id] },
            courseRequest with { Slug = "programa-anidado", CollectionKind = "program", ItemIds = [program.Id] }
        })
            Assert.Equal("collection_items_invalid", (await service.CreateAsync(Guid.NewGuid(), bad, Token)).Error);
        Assert.Equal("collection_items_invalid", (await service.UpdateAsync(Guid.NewGuid(), course.Id, Change(course, "published") with { ItemIds = [course.Id] }, Token)).Error);
        second = (await service.UpdateAsync(Guid.NewGuid(), second.Id, Change(second, "archived"), Token)).Value!;
        Assert.Null(await service.GetPublishedWorkAsync(second.Slug, Token));
        Assert.Null(await service.GetPublishedWorkAsync(course.Slug, Token));
        Assert.Null(await service.GetPublishedWorkAsync(program.Slug, Token));
        Assert.Equal(first.Id, Assert.Single((await service.GetPublishedAsync(course.Slug, Token))!.Items!).Id);
        Assert.Equal("collection_items_invalid", (await service.UpdateAsync(Guid.NewGuid(), course.Id, Change(course, "published"), Token)).Error);
        course = (await service.UpdateAsync(Guid.NewGuid(), course.Id, Change(course, "draft"), Token)).Value!;
        Assert.Equal("draft", course.Status); // A broken collection can still be withdrawn.
        Assert.Equal("collection_items_invalid", (await service.CreateAsync(Guid.NewGuid(), courseRequest with { Slug = "programa-curso-retirado", CollectionKind = "program", ItemIds = [course.Id] }, Token)).Error);
        course = (await service.UpdateAsync(Guid.NewGuid(), course.Id, Change(course, "published") with { ItemIds = [first.Id] }, Token)).Value!;
        Assert.Single((await service.GetPublishedWorkAsync(course.Slug, Token))!.Items);
        Assert.NotNull(await service.GetPublishedWorkAsync(program.Slug, Token));
        Assert.Equal("slug_immutable", (await service.UpdateAsync(Guid.NewGuid(), course.Id, Change(course, "published") with { Slug = "otra-ruta" }, Token)).Error);
    }
    [Fact]
    public async Task AuditIsPaginatedFilteredRedactedAndAppendOnlyInBothOrmAndRuntimePrivileges()
    {
        await database.ResetAsync(Token);
        var clock = new CatalogClock();
        await using var context = database.Context();
        var service = new CatalogService(context, clock);
        var draft = (await service.CreateAsync(Guid.NewGuid(), Reading("auditoria-obra"), Token)).Value!;
        var from = clock.GetUtcNow();
        clock.Advance();
        var item = (await service.UpdateAsync(Guid.NewGuid(), draft.Id, Change(draft, "published"), Token)).Value!;
        clock.Advance();
        item = (await service.UpdateAsync(Guid.NewGuid(), item.Id, Change(item, "draft"), Token)).Value!;
        clock.Advance();
        item = (await service.UpdateAsync(Guid.NewGuid(), item.Id, Change(item, "published"), Token)).Value!;
        clock.Advance();
        item = (await service.UpdateAsync(Guid.NewGuid(), item.Id, Change(item, "archived"), Token)).Value!;
        clock.Advance();
        item = (await service.UpdateAsync(Guid.NewGuid(), item.Id, Change(item, "draft"), Token)).Value!;
        var all = (await service.ListAuditAsync(item.Id, 1, 2, Token))!;
        Assert.Equal(6, all.Total); Assert.Equal(2, all.Items.Length);
        var next = (await service.ListAuditAsync(item.Id, 2, 2, Token))!;
        Assert.Empty(all.Items.Select(x => x.Id).Intersect(next.Items.Select(x => x.Id)));
        Assert.Equal("content.restored", all.Items[0].Action);
        Assert.Equal(2, (await service.ListModuleAuditAsync(from, clock.GetUtcNow(), "content.published", item.Id, 1, 20, Token)).Total);
        Assert.Equal(6, (await service.ListModuleAuditAsync(from, null, null, null, 1, 20, Token)).Total);
        Assert.Empty((await service.ListModuleAuditAsync(clock.GetUtcNow().AddDays(1), null, null, null, 1, 20, Token)).Items);
        Assert.Empty((await service.ListModuleAuditAsync(null, null, null, Guid.NewGuid(), 1, 20, Token)).Items);
        Assert.Null(await service.ListAuditAsync(Guid.NewGuid(), 1, 20, Token));
        Assert.Empty((await service.ListAuditAsync(item.Id, 10, 20, Token))!.Items);
        var audit = await context.Audit.FirstAsync(Token);
        audit.Action = "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync(Token));
        Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
        context.ChangeTracker.Clear();
        audit = await context.Audit.FirstAsync(Token);
        context.Audit.Remove(audit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync(Token));
        context.ChangeTracker.Clear();
        foreach (var sql in new[] { "UPDATE catalog.\"Audit\" SET \"Action\"='changed'", "DELETE FROM catalog.\"Audit\"" })
        {
            await using var connection = new NpgsqlConnection(database.RuntimeConnection); await connection.OpenAsync(Token);
            await using var command = new NpgsqlCommand(sql, connection);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Token))).SqlState);
        }
        Assert.Equal(6, await context.Audit.CountAsync(Token));
    }
    [Fact]
    public async Task ConcurrentRestrictedEditsHaveOneWinnerAndPersistNoOrphanAudit()
    {
        await database.ResetAsync(Token);
        AdminContentView initial;
        await using (var context = database.Context())
            initial = await Publish(new CatalogService(context, TimeProvider.System), Reading("obra-concurrente"));
        var barrier = new SaveBarrier();
        async Task<CatalogResult<AdminContentView>> Edit(string text)
        {
            await using var context = database.Context(interceptor: barrier);
            return await new CatalogService(context, TimeProvider.System).UpdateAsync(Guid.NewGuid(), initial.Id, Change(initial, "published") with { WorkText = text }, Token);
        }
        var outcomes = await Task.WhenAll(Edit("Texto restringido A"), Edit("Texto restringido B"));
        Assert.Single(outcomes, x => x.Succeeded); Assert.Single(outcomes, x => x.Error == "concurrency_conflict");
        await using var final = database.Context();
        var row = await final.Contents.SingleAsync(Token);
        Assert.Equal(outcomes.Single(x => x.Succeeded).Value!.WorkText, row.WorkText);
        Assert.Equal(3, await final.Audit.CountAsync(Token));
        Assert.All(await final.Audit.ToArrayAsync(Token), x => Assert.DoesNotContain("Texto restringido", x.Changes));
    }
    [Fact]
    public async Task CrossedCollectionConversionReturnsConflictWithoutCycleOrOrphanAudit()
    {
        await database.ResetAsync(Token);
        AdminContentView first;
        AdminContentView second;
        await using (var context = database.Context())
        {
            var service = new CatalogService(context, TimeProvider.System);
            first = await Publish(service, Reading("cruce-primera"));
            second = await Publish(service, Reading("cruce-segunda"));
        }
        // Both validations hold SHARE on the other row before either updates itself.
        // PostgreSQL chooses one deadlock victim; its whole update must become a 409.
        var barrier = new SaveBarrier();
        async Task<CatalogResult<AdminContentView>> Convert(AdminContentView own, AdminContentView child)
        {
            await using var context = database.Context(interceptor: barrier);
            var request = Change(own, "published") with { Category = "cursos", WorkText = null, CollectionKind = "course", ItemIds = [child.Id] };
            var result = await new CatalogService(context, TimeProvider.System).UpdateAsync(Guid.NewGuid(), own.Id, request, Token);
            Assert.Null(context.Database.CurrentTransaction);
            if (!result.Succeeded)
            {
                Assert.Equal("concurrency_conflict", result.Error);
                Assert.Equal(409, result.Status);
                Assert.Empty(context.ChangeTracker.Entries());
            }
            Assert.Equal(2, await context.Contents.AsNoTracking().CountAsync(Token));
            return result;
        }
        var outcomes = await Task.WhenAll(Convert(first, second), Convert(second, first));
        var winner = Assert.Single(outcomes, x => x.Succeeded).Value!;
        Assert.Single(outcomes, x => x.Error == "concurrency_conflict" && x.Status == 409);
        await using var final = database.Context();
        Assert.Equal(5, await final.Audit.CountAsync(Token)); // Two creates, two publishes, one update.
        Assert.Single(await final.Audit.Where(x => x.Action == "content.updated").ToArrayAsync(Token));
        var rows = await final.Contents.AsNoTracking().ToArrayAsync(Token);
        var course = Assert.Single(rows, x => x.CollectionKind == "course");
        var reading = Assert.Single(rows, x => x.CollectionKind is null);
        Assert.Equal(winner.Id, course.Id);
        Assert.Equal([reading.Id], course.ItemIds); // The loser stays a usable simple work.
        Assert.NotNull(reading.WorkText);
        var serviceAfterRace = new CatalogService(final, TimeProvider.System);
        var work = (await serviceAfterRace.GetPublishedWorkAsync(course.Slug, Token))!;
        Assert.Equal(reading.Id, Assert.Single(work.Items).Id);
        Assert.Null(work.WorkText); Assert.Null(work.YouTubeId);
        var metadata = JsonSerializer.Serialize(new { course = await serviceAfterRace.GetPublishedAsync(course.Slug, Token), reading = await serviceAfterRace.GetPublishedAsync(reading.Slug, Token) });
        foreach (var secret in new[] { "OBRA RESTRINGIDA", "WorkText", "YouTubeId" }) Assert.DoesNotContain(secret, metadata);
    }
    [Fact]
    public async Task WrappedSerializationFailureReturnsConflictAfterRollbackAndKeepsContextUsable()
    {
        await database.ResetAsync(Token);
        AdminContentView initial;
        await using (var setup = database.Context())
            initial = await Publish(new CatalogService(setup, TimeProvider.System), Reading("serialization-conflict"));
        await using var context = database.Context(interceptor: new WrappedFailure(PostgresErrorCodes.SerializationFailure));
        var result = await new CatalogService(context, TimeProvider.System).UpdateAsync(Guid.NewGuid(), initial.Id,
            Change(initial, "published") with { WorkText = "Replacement must not persist." }, Token);
        Assert.Equal("concurrency_conflict", result.Error);
        Assert.Equal(409, result.Status);
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Empty(context.ChangeTracker.Entries());
        var persisted = await context.Contents.AsNoTracking().SingleAsync(Token);
        Assert.Equal(initial.Version, persisted.Version);
        Assert.Equal(initial.WorkText, persisted.WorkText);
        Assert.Equal(2, await context.Audit.CountAsync(Token));
    }
    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation)]
    [InlineData(PostgresErrorCodes.ConnectionFailure)]
    [InlineData(PostgresErrorCodes.QueryCanceled)]
    public async Task WrappedNonConcurrencyDatabaseFailuresRemainFailures(string sqlState)
    {
        await database.ResetAsync(Token);
        AdminContentView initial;
        await using (var setup = database.Context())
            initial = await Publish(new CatalogService(setup, TimeProvider.System), Reading("non-conflict-failure"));
        await using var context = database.Context(interceptor: new WrappedFailure(sqlState));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CatalogService(context, TimeProvider.System).UpdateAsync(Guid.NewGuid(), initial.Id,
                Change(initial, "published") with { WorkText = "Replacement must not persist." }, Token));
        var databaseError = Assert.IsType<DbUpdateException>(exception.InnerException);
        Assert.Equal(sqlState, Assert.IsType<PostgresException>(databaseError.InnerException).SqlState);
        Assert.Null(context.Database.CurrentTransaction);
        var persisted = await context.Contents.AsNoTracking().SingleAsync(Token);
        Assert.Equal(initial.Version, persisted.Version);
        Assert.Equal(initial.WorkText, persisted.WorkText);
        Assert.Equal(2, await context.Audit.CountAsync(Token));
    }
    [Fact]
    public async Task DatabaseConstraintsRejectForgedMediaAndIllegalCollectionShape()
    {
        await database.ResetAsync(Token);
        await using var context = database.Context();
        var service = new CatalogService(context, TimeProvider.System);
        var video = await Publish(service, Reading("medio-validado") with { Category = "videos", WorkText = null, YouTubeId = "dQw4w9WgXcQ" });
        foreach (var sql in new[]
        {
            $"UPDATE catalog.\"Contents\" SET \"YouTubeId\"='<script>abc' WHERE \"Id\"='{video.Id}'",
            $"UPDATE catalog.\"Contents\" SET \"Category\"='lecturas' WHERE \"Id\"='{video.Id}'",
            $"UPDATE catalog.\"Contents\" SET \"WorkText\"='Texto privado' WHERE \"Id\"='{video.Id}'",
            $"UPDATE catalog.\"Contents\" SET \"ItemIds\"=ARRAY['{video.Id}'::uuid] WHERE \"Id\"='{video.Id}'",
            $"UPDATE catalog.\"Contents\" SET \"Category\"='cursos', \"YouTubeId\"=NULL, \"CollectionKind\"='program' WHERE \"Id\"='{video.Id}'"
        })
        {
            await using var connection = new NpgsqlConnection(database.RuntimeConnection); await connection.OpenAsync(Token);
            await using var command = new NpgsqlCommand(sql, connection);
            Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Token))).SqlState);
        }
        Assert.Equal(video.YouTubeId, (await service.GetPublishedWorkAsync(video.Slug, Token))!.YouTubeId);
    }
    private sealed class WrappedFailure(string sqlState) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Synthetic execution strategy failure.",
                new DbUpdateException("Synthetic save failure.", new PostgresException("Synthetic database failure.", "ERROR", "ERROR", sqlState)));
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
