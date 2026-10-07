using System.Data.Common;
using Acropolis.Catalog.Application;
using Acropolis.Catalog.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Acropolis.Catalog.IntegrationTests;

[Collection("CatalogPostgres")]
public sealed class CatalogPaginationSnapshotTests(CatalogFixture database)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CountAndPageShareOneSnapshotDuringCommittedEditorialWithdrawal(bool administrative)
    {
        await database.ResetAsync(Token);
        var clock = new CatalogClock();
        var actor = Guid.NewGuid();
        AdminContentView withdrawn;
        AdminContentView remaining;
        await using (var setup = database.Context())
        {
            var editor = new CatalogService(setup, clock);
            withdrawn = await PublishAsync(editor, actor, "snapshot-withdrawn");
            remaining = await PublishAsync(editor, actor, "snapshot-remaining");
        }
        var interceptor = new AfterCatalogCount(async cancellationToken =>
        {
            await using var writer = database.Context();
            var editor = new CatalogService(writer, clock);
            clock.Advance();
            var result = await editor.UpdateAsync(actor, withdrawn.Id, Change(withdrawn, "draft"), cancellationToken);
            Assert.Null(result.Error);
            Assert.Equal("draft", Assert.IsType<AdminContentView>(result.Value).Status);
        });
        await using var context = database.Context(interceptor: interceptor);
        var service = new CatalogService(context, clock);
        var snapshot = await ListAsync(service, administrative);
        Assert.Equal(1, interceptor.Calls);
        Assert.Equal(2, snapshot.Total);
        Assert.Equal(2, snapshot.Ids.Length);
        Assert.Contains(withdrawn.Id, snapshot.Ids);
        Assert.Contains(remaining.Id, snapshot.Ids);

        var current = await ListAsync(service, administrative);
        Assert.Equal(1, current.Total);
        Assert.Equal(remaining.Id, Assert.Single(current.Ids));
        Assert.Null(await service.GetPublishedAsync(withdrawn.Slug, Token));
        var audit = await service.ListAuditAsync(withdrawn.Id, 1, 20, Token);
        Assert.Contains(Assert.IsType<ContentAuditPage>(audit).Items, item => item.Action == "content.withdrawn" && item.ActorId == actor);
    }

    private async Task<AdminContentView> PublishAsync(CatalogService service, Guid actor, string slug)
    {
        var created = await service.CreateAsync(actor, new(slug, "Lectura de QA", "Resumen de QA", "Sinopsis de QA", "lecturas"), Token);
        Assert.Null(created.Error);
        var draft = Assert.IsType<AdminContentView>(created.Value);
        var published = await service.UpdateAsync(actor, draft.Id, Change(draft, "published"), Token);
        Assert.Null(published.Error);
        return Assert.IsType<AdminContentView>(published.Value);
    }

    private async Task<(int Total, Guid[] Ids)> ListAsync(CatalogService service, bool administrative)
    {
        if (administrative)
        {
            var page = await service.ListAdminAsync(null, "lecturas", "published", 1, 20, Token);
            return (page.Total, page.Items.Select(item => item.Id).ToArray());
        }
        var published = await service.ListPublishedAsync(null, "lecturas", 1, 20, Token);
        return (published.Total, published.Items.Select(item => item.Id).ToArray());
    }

    private static UpdateContentRequest Change(AdminContentView item, string status) =>
        new(item.Version, status, item.Slug, item.Title, item.Summary, item.Body, item.Category);

    private sealed class AfterCatalogCount(Func<CancellationToken, Task> action) : DbCommandInterceptor
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("count(", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("catalog.\"Contents\"", StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref calls, 1, 0) == 0)
                await action(cancellationToken);
            return result;
        }
    }
}
