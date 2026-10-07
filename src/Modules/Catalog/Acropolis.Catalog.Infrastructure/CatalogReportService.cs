using Acropolis.Catalog.Application;
using Microsoft.EntityFrameworkCore;

namespace Acropolis.Catalog.Infrastructure;

public sealed class CatalogReportService(CatalogDbContext database, TimeProvider clock) : ICatalogReportService
{
    private static readonly string[] Statuses = ["draft", "published", "archived"];
    private static readonly string[] Kinds = ["work", "course", "program"];

    public async Task<CatalogReportView> GetCurrentAsync(CancellationToken token)
    {
        var generatedUtc = clock.GetUtcNow().ToUniversalTime();
        var groups = await database.Contents.AsNoTracking()
            .GroupBy(x => new { x.Category, x.Status, Kind = x.CollectionKind ?? "work" })
            .Select(x => new { x.Key.Category, x.Key.Status, x.Key.Kind, Count = x.LongCount() })
            .ToArrayAsync(token);
        return new CatalogReportView(generatedUtc, groups.Sum(x => x.Count),
            Statuses.Select(status => new CatalogReportCount(status, groups.Where(x => x.Status == status).Sum(x => x.Count))).ToArray(),
            CatalogRules.Categories.SelectMany(category => Statuses.Select(status =>
                new CatalogCategoryStateCount(category.Id, status, groups.Where(x => x.Category == category.Id && x.Status == status).Sum(x => x.Count)))).ToArray(),
            Kinds.SelectMany(kind => Statuses.Select(status =>
                new CatalogKindStateCount(kind, status, groups.Where(x => x.Kind == kind && x.Status == status).Sum(x => x.Count)))).ToArray());
    }
}
