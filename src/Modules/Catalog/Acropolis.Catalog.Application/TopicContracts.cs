namespace Acropolis.Catalog.Application;

public sealed record CreateTopicRequest(string Slug, string Name);
public sealed record UpdateTopicRequest(string Version, string Name);
public sealed record TopicStateRequest(string Version, string Status);
public sealed record MoveTopicRequest(string DirectoryVersion, Guid Id, Guid? BeforeId);
public sealed record AssignContentTopicsRequest(string ContentVersion, Guid[] TopicIds);
public sealed record AdminTopicView(Guid Id, string Slug, string Name, string Status, int Position, string Version);
public sealed record PublicTopicView(Guid Id, string Slug, string Name, int Position);
public sealed record AdminTopicPage(AdminTopicView[] Items, int Total, int Page, int PageSize, string DirectoryVersion);
public sealed record PublicTopicPage(PublicTopicView[] Items, int Total, int Page, int PageSize);
public sealed record ContentTopicsView(Guid ContentId, string ContentVersion, AdminTopicView[] Items);
public sealed record TopicAuditView(Guid Id, Guid ActorId, Guid TopicId, string Action, string Changes, DateTimeOffset CreatedUtc);
public sealed record TopicAuditPage(TopicAuditView[] Items, int Total, int Page, int PageSize);
public interface ITopicService
{
    Task<PublicTopicPage> ListPublicAsync(int page, int pageSize, CancellationToken token);
    Task<AdminTopicPage> ListAdminAsync(string? search, string? status, int page, int pageSize, CancellationToken token);
    Task<AdminTopicView?> GetAdminAsync(Guid id, CancellationToken token);
    Task<TopicAuditPage?> ListAuditAsync(Guid id, int page, int pageSize, CancellationToken token);
    Task<ContentPage> ListPublishedByTopicAsync(string topic, string? search, string? category, int page, int pageSize, CancellationToken token);
    Task<ContentTopicsView?> GetContentTopicsAsync(Guid id, CancellationToken token);
    Task<CatalogResult<AdminTopicView>> CreateAsync(Guid actorId, CreateTopicRequest request, CancellationToken token);
    Task<CatalogResult<AdminTopicView>> UpdateAsync(Guid actorId, Guid id, UpdateTopicRequest request, CancellationToken token);
    Task<CatalogResult<AdminTopicView>> SetStateAsync(Guid actorId, Guid id, TopicStateRequest request, CancellationToken token);
    Task<CatalogResult<AdminTopicView>> MoveAsync(Guid actorId, MoveTopicRequest request, CancellationToken token);
    Task<CatalogResult<ContentTopicsView>> AssignAsync(Guid actorId, Guid id, AssignContentTopicsRequest request, CancellationToken token);
}
