namespace Acropolis.Catalog.Application;

// Body is public synopsis. WorkText and YouTubeId belong only to editorial and protected consumption DTOs.
public sealed record CategoryView(string Id, string Label);
public sealed record CreateContentRequest(string Slug, string Title, string Summary, string Body, string Category, string? CoverAsset = null, int? DurationSeconds = null, string? Author = null, string[]? Tags = null, string? WorkText = null, string? YouTubeId = null, string? CollectionKind = null, Guid[]? ItemIds = null);
public sealed record UpdateContentRequest(string Version, string Status, string Slug, string Title, string Summary, string Body, string Category, string? CoverAsset = null, int? DurationSeconds = null, string? Author = null, string[]? Tags = null, string? WorkText = null, string? YouTubeId = null, string? CollectionKind = null, Guid[]? ItemIds = null);
public sealed record ContentSummary(Guid Id, string Slug, string Title, string Summary, string Category, string? CoverAsset, int? DurationSeconds, DateTimeOffset? PublishedUtc, string? Author = null, string[]? Tags = null, string? CollectionKind = null);
public sealed record ContentDetail(Guid Id, string Slug, string Title, string Summary, string Body, string Category, string? CoverAsset, int? DurationSeconds, DateTimeOffset? PublishedUtc, DateTimeOffset UpdatedUtc, string? Author = null, string[]? Tags = null, string? CollectionKind = null, ContentSummary[]? Items = null);
public sealed record AdminContentSummary(Guid Id, string Slug, string Title, string Summary, string Category, string? CoverAsset, int? DurationSeconds, DateTimeOffset? PublishedUtc, string Status, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, string Version, string? Author = null, string[]? Tags = null, string? CollectionKind = null);
public sealed record AdminContentView(Guid Id, string Slug, string Title, string Summary, string Body, string Category, string? CoverAsset, int? DurationSeconds, DateTimeOffset? PublishedUtc, string Status, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, string Version, string? Author = null, string[]? Tags = null, string? WorkText = null, string? YouTubeId = null, string? CollectionKind = null, Guid[]? ItemIds = null);
public sealed record ContentWorkView(Guid Id, string Slug, string Title, string Category, string? WorkText, string? YouTubeId, string? CollectionKind, ContentSummary[] Items, string Version = "");
public sealed record ContentAuditView(Guid Id, Guid ActorId, Guid ContentId, string Action, string Changes, DateTimeOffset CreatedUtc);
public sealed record ContentPage(ContentSummary[] Items, int Total, int Page, int PageSize);
public sealed record AdminContentPage(AdminContentSummary[] Items, int Total, int Page, int PageSize);
public sealed record ContentAuditPage(ContentAuditView[] Items, int Total, int Page, int PageSize);
public interface ICatalogService
{
    Task<ContentPage> ListPublishedAsync(string? search, string? category, int page, int pageSize, CancellationToken token);
    Task<ContentDetail?> GetPublishedAsync(string slug, CancellationToken token);
    // Permission-neutral module contract: the host composes account and subscription authorization.
    Task<ContentWorkView?> GetPublishedWorkAsync(string slug, CancellationToken token);
    Task<AdminContentPage> ListAdminAsync(string? search, string? category, string? status, int page, int pageSize, CancellationToken token);
    Task<AdminContentView?> GetAdminAsync(Guid id, CancellationToken token);
    Task<AdminContentSummary?> GetAdminSummaryAsync(Guid id, CancellationToken token);
    Task<ContentAuditPage?> ListAuditAsync(Guid id, int page, int pageSize, CancellationToken token);
    Task<ContentAuditPage> ListModuleAuditAsync(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? action, Guid? contentId, int page, int pageSize, CancellationToken token);
    Task<CatalogResult<AdminContentView>> CreateAsync(Guid actorId, CreateContentRequest request, CancellationToken token);
    Task<CatalogResult<AdminContentView>> UpdateAsync(Guid actorId, Guid id, UpdateContentRequest request, CancellationToken token);
}
