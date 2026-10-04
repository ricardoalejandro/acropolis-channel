namespace Acropolis.Catalog.Application;

// Body is a public editorial synopsis, never a complete work or restricted material.
public sealed record CategoryView(string Id, string Label);
public sealed record CreateContentRequest(string Slug, string Title, string Summary, string Body, string Category, string? CoverAsset = null, int? DurationSeconds = null);
public sealed record UpdateContentRequest(string Version, string Status, string Slug, string Title, string Summary, string Body, string Category, string? CoverAsset = null, int? DurationSeconds = null);
public sealed record ContentSummary(Guid Id, string Slug, string Title, string Summary, string Category, string? CoverAsset, int? DurationSeconds, DateTimeOffset? PublishedUtc);
public sealed record ContentDetail(Guid Id, string Slug, string Title, string Summary, string Body, string Category, string? CoverAsset, int? DurationSeconds, DateTimeOffset? PublishedUtc, DateTimeOffset UpdatedUtc);
public sealed record AdminContentSummary(Guid Id, string Slug, string Title, string Summary, string Category, string? CoverAsset, int? DurationSeconds, DateTimeOffset? PublishedUtc, string Status, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, string Version);
public sealed record AdminContentView(Guid Id, string Slug, string Title, string Summary, string Body, string Category, string? CoverAsset, int? DurationSeconds, DateTimeOffset? PublishedUtc, string Status, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, string Version);
public sealed record ContentPage(ContentSummary[] Items, int Total, int Page, int PageSize);
public sealed record AdminContentPage(AdminContentSummary[] Items, int Total, int Page, int PageSize);
public interface ICatalogService
{
    Task<ContentPage> ListPublishedAsync(string? search, string? category, int page, int pageSize, CancellationToken token);
    Task<ContentDetail?> GetPublishedAsync(string slug, CancellationToken token);
    Task<AdminContentPage> ListAdminAsync(string? search, string? category, string? status, int page, int pageSize, CancellationToken token);
    Task<AdminContentView?> GetAdminAsync(Guid id, CancellationToken token);
    Task<CatalogResult<AdminContentView>> CreateAsync(Guid actorId, CreateContentRequest request, CancellationToken token);
    Task<CatalogResult<AdminContentView>> UpdateAsync(Guid actorId, Guid id, UpdateContentRequest request, CancellationToken token);
}
