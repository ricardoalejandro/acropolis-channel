namespace Acropolis.Catalog.Application;

public sealed record CatalogReportCount(string Key, long Count);
public sealed record CatalogCategoryStateCount(string Category, string Status, long Count);
public sealed record CatalogKindStateCount(string Kind, string Status, long Count);
public sealed record CatalogReportView(DateTimeOffset GeneratedUtc, long Total,
    CatalogReportCount[] ByStatus, CatalogCategoryStateCount[] ByCategoryAndStatus,
    CatalogKindStateCount[] ByKindAndStatus)
{
    public string Scope => "current";
}
public interface ICatalogReportService
{
    Task<CatalogReportView> GetCurrentAsync(CancellationToken token);
}
