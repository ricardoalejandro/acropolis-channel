using System.Data.Common;
using Acropolis.Catalog.Application;
using Acropolis.Catalog.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Acropolis.Catalog.IntegrationTests;

[Collection("CatalogPostgres")]
public sealed class OperationalReportTests(CatalogFixture database)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EmptyCatalogSnapshotIncludesSixCategoriesAndThreeKindsWithoutWriting()
    {
        await database.ResetAsync(Token);
        await using var context = database.Context();
        var clock = new CatalogClock();
        var report = await new CatalogReportService(context, clock).GetCurrentAsync(Token);
        Assert.Equal(clock.GetUtcNow(), report.GeneratedUtc);
        Assert.Equal(TimeSpan.Zero, report.GeneratedUtc.Offset);
        Assert.Equal("current", report.Scope);
        Assert.Equal(0L, report.Total);
        Assert.Equal(new[] { "draft", "published", "archived" }, report.ByStatus.Select(x => x.Key));
        Assert.Equal(18, report.ByCategoryAndStatus.Length);
        Assert.Equal(9, report.ByKindAndStatus.Length);
        Assert.Equal(CatalogRules.Categories.Select(x => x.Id), report.ByCategoryAndStatus.Select(x => x.Category).Distinct());
        Assert.Equal(new[] { "work", "course", "program" }, report.ByKindAndStatus.Select(x => x.Kind).Distinct());
        Assert.All(report.ByStatus, x => Assert.Equal(0L, x.Count));
        Assert.All(report.ByCategoryAndStatus, x => Assert.Equal(0L, x.Count));
        Assert.All(report.ByKindAndStatus, x => Assert.Equal(0L, x.Count));
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(0, await context.Audit.CountAsync(Token));
    }

    [Fact]
    public async Task ActualCurrentStatesIncludeWithdrawnWorksAndCollectionsWithoutReadingRestrictedContent()
    {
        await database.ResetAsync(Token);
        var now = new CatalogClock().GetUtcNow();
        var states = new[] { "draft", "published", "archived" };
        var rows = new List<EditorialContent>();
        foreach (var category in CatalogRules.Categories)
            foreach (var state in states)
            {
                var row = Content(category.Id, state, now);
                row.WorkText = category.Id == "lecturas" ? "Synthetic private work that must not be projected." : null;
                row.YouTubeId = CatalogRules.VideoCategory(category.Id) ? "AbCdEfGh123" : null;
                rows.Add(row);
            }
        foreach (var kind in new[] { "course", "program" })
            foreach (var state in states)
            {
                var row = Content("cursos", state, now);
                row.CollectionKind = kind;
                row.ItemIds = [kind == "course" ? rows[0].Id : rows.First(x => x.CollectionKind == "course").Id];
                rows.Add(row);
            }
        await using (var seed = database.Context())
        {
            seed.Contents.AddRange(rows);
            await seed.SaveChangesAsync(Token);
        }
        var projection = new ReportProjection();
        await using var context = database.Context(interceptor: projection);
        var before = await context.Database.SqlQueryRaw<string>("SELECT xmin::text AS \"Value\" FROM catalog.\"Contents\" ORDER BY \"Id\"").ToArrayAsync(Token);
        var report = await new CatalogReportService(context, new CatalogClock()).GetCurrentAsync(Token);
        Assert.Equal(24L, report.Total);
        Assert.Equal(new long[] { 8, 8, 8 }, report.ByStatus.Select(x => x.Count));
        Assert.Equal(report.Total, report.ByStatus.Sum(x => x.Count));
        Assert.Equal(report.Total, report.ByCategoryAndStatus.Sum(x => x.Count));
        Assert.Equal(report.Total, report.ByKindAndStatus.Sum(x => x.Count));
        foreach (var group in report.ByCategoryAndStatus) Assert.Equal(group.Category == "cursos" ? 3L : 1L, group.Count);
        foreach (var group in report.ByKindAndStatus) Assert.Equal(group.Kind == "work" ? 6L : 1L, group.Count);
        Assert.Equal(1, projection.Aggregates);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(before, await context.Database.SqlQueryRaw<string>("SELECT xmin::text AS \"Value\" FROM catalog.\"Contents\" ORDER BY \"Id\"").ToArrayAsync(Token));
        Assert.Equal(0, await context.Audit.CountAsync(Token));
        Assert.Equal(3, context.Database.GetCommandTimeout());
    }

    [Fact]
    public async Task CancellationDoesNotWriteAndDoesNotPreventAnExplicitRetry()
    {
        await database.ResetAsync(Token);
        await using var context = database.Context();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var service = new CatalogReportService(context, new CatalogClock());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetCurrentAsync(cancelled.Token));
        Assert.Equal(0L, (await service.GetCurrentAsync(Token)).Total);
        Assert.Equal(0, await context.Audit.CountAsync(Token));
    }

    private static EditorialContent Content(string category, string state, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Slug = "report-" + Guid.NewGuid().ToString("N"),
        Title = "Synthetic editorial report",
        Summary = "Synthetic public summary",
        Body = "Synthetic public synopsis",
        Category = category,
        Status = state,
        CreatedUtc = now.AddYears(-1),
        UpdatedUtc = now.AddDays(-1),
        PublishedUtc = now.AddMonths(-6)
    };
    private sealed class ReportProjection : DbCommandInterceptor
    {
        public int Aggregates { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("GROUP BY", StringComparison.Ordinal))
            {
                Aggregates++;
                foreach (var field in new[] { "WorkText", "YouTubeId", "Body", "Title", "Author", "ItemIds" })
                    Assert.DoesNotContain("\"" + field + "\"", command.CommandText, StringComparison.Ordinal);
            }
            return ValueTask.FromResult(result);
        }
    }
}
