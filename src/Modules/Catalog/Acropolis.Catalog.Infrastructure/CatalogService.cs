using System.Data;
using System.Linq.Expressions;
using System.Text.Json;
using Acropolis.Catalog.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Acropolis.Catalog.Infrastructure;

public sealed class CatalogService(CatalogDbContext database, TimeProvider clock) : ICatalogService
{
    private static readonly Expression<Func<EditorialContent, ContentSummary>> SummaryProjection = x =>
        new(x.Id, x.Slug, x.Title, x.Summary, x.Category, x.CoverAsset, x.DurationSeconds, x.PublishedUtc, x.Author, x.Tags, x.CollectionKind);
    private sealed record PublicEntry(ContentDetail Detail, Guid[] ItemIds);
    private sealed record Reference(ContentSummary Summary, Guid[] ItemIds, bool Ready);
    public async Task<ContentPage> ListPublishedAsync(string? search, string? category, int page, int pageSize, CancellationToken token)
    {
        var query = Filter(database.Contents.AsNoTracking().Where(x => x.Status == "published"), search, category);
        var total = await query.CountAsync(token);
        var entries = await query.OrderByDescending(x => x.PublishedUtc).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(SummaryProjection).ToArrayAsync(token);
        return new(entries, total, page, pageSize);
    }
    public async Task<ContentDetail?> GetPublishedAsync(string slug, CancellationToken token)
    {
        var entry = await database.Contents.AsNoTracking().Where(x => x.Slug == slug && x.Status == "published")
            .Select(x => new PublicEntry(new ContentDetail(x.Id, x.Slug, x.Title, x.Summary, x.Body, x.Category, x.CoverAsset, x.DurationSeconds, x.PublishedUtc, x.UpdatedUtc, x.Author, x.Tags, x.CollectionKind, Array.Empty<ContentSummary>()), x.ItemIds)).SingleOrDefaultAsync(token);
        if (entry is null) return null;
        var references = await LoadReferencesAsync(entry.ItemIds, false, token);
        return entry.Detail with { Items = Ordered(entry.ItemIds, references) };
    }
    public async Task<ContentWorkView?> GetPublishedWorkAsync(string slug, CancellationToken token)
    {
        // One snapshot prevents assembling a work from publication states at different instants.
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var entry = await database.Contents.AsNoTracking().SingleOrDefaultAsync(x => x.Slug == slug && x.Status == "published", token);
        if (entry is null) return null;
        if (entry.CollectionKind is null)
        {
            if (!HasSimpleWork(entry)) return null;
            return new(entry.Id, entry.Slug, entry.Title, entry.Category, entry.WorkText, entry.YouTubeId, null, [], entry.Version);
        }
        if (entry.ItemIds.Length == 0) return null;
        var references = await LoadReferencesAsync(entry.ItemIds, false, token);
        if (!await AvailableCollectionAsync(entry.CollectionKind, entry.ItemIds, references, false, token)) return null;
        return new(entry.Id, entry.Slug, entry.Title, entry.Category, null, null, entry.CollectionKind, Ordered(entry.ItemIds, references), entry.Version);
    }
    public async Task<AdminContentPage> ListAdminAsync(string? search, string? category, string? status, int page, int pageSize, CancellationToken token)
    {
        var query = Filter(database.Contents.AsNoTracking(), search, category);
        if (status is not null) query = query.Where(x => x.Status == status);
        var total = await query.CountAsync(token);
        var entries = await query.OrderByDescending(x => x.UpdatedUtc).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new AdminContentSummary(x.Id, x.Slug, x.Title, x.Summary, x.Category, x.CoverAsset, x.DurationSeconds, x.PublishedUtc, x.Status, x.CreatedUtc, x.UpdatedUtc, x.Version, x.Author, x.Tags, x.CollectionKind)).ToArrayAsync(token);
        return new(entries, total, page, pageSize);
    }
    public async Task<AdminContentView?> GetAdminAsync(Guid id, CancellationToken token)
    {
        var entry = await database.Contents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        return entry is null ? null : View(entry);
    }
    public async Task<AdminContentSummary?> GetAdminSummaryAsync(Guid id, CancellationToken token) =>
        await database.Contents.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new AdminContentSummary(x.Id, x.Slug, x.Title, x.Summary, x.Category, x.CoverAsset, x.DurationSeconds, x.PublishedUtc, x.Status, x.CreatedUtc, x.UpdatedUtc, x.Version, x.Author, x.Tags, x.CollectionKind)).SingleOrDefaultAsync(token);
    public async Task<ContentAuditPage?> ListAuditAsync(Guid id, int page, int pageSize, CancellationToken token)
    {
        if (!await database.Contents.AsNoTracking().AnyAsync(x => x.Id == id, token)) return null;
        return await ListModuleAuditAsync(null, null, null, id, page, pageSize, token);
    }
    public async Task<ContentAuditPage> ListModuleAuditAsync(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? action, Guid? contentId, int page, int pageSize, CancellationToken token)
    {
        var query = database.Audit.AsNoTracking();
        if (fromUtc is not null) query = query.Where(x => x.CreatedUtc >= fromUtc);
        if (toUtc is not null) query = query.Where(x => x.CreatedUtc <= toUtc);
        if (action is not null) query = query.Where(x => x.Action == action);
        if (contentId is not null) query = query.Where(x => x.ContentId == contentId);
        var total = await query.CountAsync(token);
        var entries = await query.OrderByDescending(x => x.CreatedUtc).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new ContentAuditView(x.Id, x.ActorId, x.ContentId, x.Action, x.Changes, x.CreatedUtc)).ToArrayAsync(token);
        return new(entries, total, page, pageSize);
    }
    public async Task<CatalogResult<AdminContentView>> CreateAsync(Guid actorId, CreateContentRequest request, CancellationToken token)
    {
        var errors = CatalogRules.Validate(request);
        if (errors.Count > 0) return CatalogResult<AdminContentView>.Fail("validation_error", 400, errors);
        var now = clock.GetUtcNow();
        var entry = new EditorialContent { Id = Guid.NewGuid(), CreatedUtc = now, UpdatedUtc = now };
        Apply(entry, request);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        if (!await ValidReferencesAsync(entry.CollectionKind, entry.ItemIds, entry.Id, token)) return InvalidReferences();
        database.Contents.Add(entry);
        database.Audit.Add(Audit(actorId, entry, "content.created", new { status = "draft", collectionKind = entry.CollectionKind, itemCount = entry.ItemIds.Length, hasWork = HasSimpleWork(entry) }, now));
        try
        {
            await database.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        catch (DbUpdateException error) when (IsSlugConflict(error)) { database.ChangeTracker.Clear(); return CatalogResult<AdminContentView>.Fail("slug_conflict", 409); }
        return new(View(entry), Status: 201);
    }
    public async Task<CatalogResult<AdminContentView>> UpdateAsync(Guid actorId, Guid id, UpdateContentRequest request, CancellationToken token)
    {
        var errors = CatalogRules.Validate(request);
        if (errors.Count > 0) return CatalogResult<AdminContentView>.Fail("validation_error", 400, errors);
        try { return await UpdateCoreAsync(actorId, id, request, token); }
        catch (Exception error) when (IsTransactionConflict(error))
        {
            // Core disposes the failed transaction before the scoped context can be reused.
            database.ChangeTracker.Clear();
            return CatalogResult<AdminContentView>.Fail("concurrency_conflict", 409);
        }
    }
    private async Task<CatalogResult<AdminContentView>> UpdateCoreAsync(Guid actorId, Guid id, UpdateContentRequest request, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var entry = await database.Contents.SingleOrDefaultAsync(x => x.Id == id, token);
        if (entry is null) return CatalogResult<AdminContentView>.Fail("not_found", 404);
        if (entry.Version != request.Version) return CatalogResult<AdminContentView>.Fail("concurrency_conflict", 409);
        if (!CatalogRules.CanTransition(entry.Status, request.Status)) return CatalogResult<AdminContentView>.Fail("invalid_transition", 409);
        if (entry.PublishedUtc is not null && entry.Slug != request.Slug) return CatalogResult<AdminContentView>.Fail("slug_immutable", 409,
            new() { ["slug"] = ["El enlace permanece estable después de la primera publicación."] });
        var ids = request.ItemIds ?? [];
        // Withdrawal/archive must remain possible after a child is withdrawn.
        var referencesChanged = entry.CollectionKind != request.CollectionKind || !entry.ItemIds.SequenceEqual(ids);
        if ((request.Status == "published" || referencesChanged) && !await ValidReferencesAsync(request.CollectionKind, ids, entry.Id, token)) return InvalidReferences();
        var before = new
        {
            status = entry.Status,
            titleChanged = entry.Title != request.Title.Trim(),
            synopsisChanged = entry.Body != request.Body.Trim() || entry.Summary != request.Summary.Trim(),
            workChanged = entry.WorkText != request.WorkText?.Trim() || entry.YouTubeId != request.YouTubeId,
            metadataChanged = entry.Author != request.Author?.Trim() || !entry.Tags.SequenceEqual((request.Tags ?? []).Select(x => x.Trim())),
            collectionChanged = entry.CollectionKind != request.CollectionKind || !entry.ItemIds.SequenceEqual(ids)
        };
        var action = entry.Status == request.Status ? "content.updated" : request.Status switch
        {
            "published" => "content.published",
            "archived" => "content.archived",
            _ => entry.Status == "archived" ? "content.restored" : "content.withdrawn"
        };
        var now = clock.GetUtcNow();
        Apply(entry, new(request.Slug, request.Title, request.Summary, request.Body, request.Category, request.CoverAsset, request.DurationSeconds, request.Author, request.Tags, request.WorkText, request.YouTubeId, request.CollectionKind, request.ItemIds));
        entry.Status = request.Status;
        entry.UpdatedUtc = now;
        if (entry.Status == "published" && entry.PublishedUtc is null) entry.PublishedUtc = now;
        entry.Version = Guid.NewGuid().ToString("N");
        database.Audit.Add(Audit(actorId, entry, action, new { before, status = entry.Status, collectionKind = entry.CollectionKind, itemCount = entry.ItemIds.Length }, now));
        try
        {
            await database.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        catch (DbUpdateConcurrencyException) { database.ChangeTracker.Clear(); return CatalogResult<AdminContentView>.Fail("concurrency_conflict", 409); }
        catch (DbUpdateException error) when (IsSlugConflict(error)) { database.ChangeTracker.Clear(); return CatalogResult<AdminContentView>.Fail("slug_conflict", 409); }
        return new(View(entry));
    }
    private async Task<bool> ValidReferencesAsync(string? kind, Guid[] ids, Guid self, CancellationToken token)
    {
        if (ids.Contains(self)) return false;
        if (kind is null || ids.Length == 0) return true;
        var references = await LoadReferencesAsync(ids, true, token);
        return await AvailableCollectionAsync(kind, ids, references, true, token);
    }
    private async Task<Reference[]> LoadReferencesAsync(Guid[] ids, bool locking, CancellationToken token)
    {
        if (ids.Length == 0) return [];
        if (locking)
            await database.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM catalog.\"Contents\" WHERE \"Id\"=ANY({ids}) ORDER BY \"Id\" FOR SHARE", token);
        // No restricted text or media identifier is selected into collection metadata.
        return await database.Contents.AsNoTracking().Where(x => ids.Contains(x.Id) && x.Status == "published")
            .Select(x => new Reference(new ContentSummary(x.Id, x.Slug, x.Title, x.Summary, x.Category, x.CoverAsset, x.DurationSeconds, x.PublishedUtc, x.Author, x.Tags, x.CollectionKind),
                x.ItemIds, x.CollectionKind == null && ((x.Category == "lecturas" && x.WorkText != null) || ((x.Category == "documentales" || x.Category == "videos" || x.Category == "podcast" || x.Category == "charlas-online") && x.YouTubeId != null)))).ToArrayAsync(token);
    }
    private async Task<bool> AvailableCollectionAsync(string kind, Guid[] ids, Reference[] references, bool locking, CancellationToken token)
    {
        if (references.Length != ids.Length) return false;
        if (kind == "course") return references.All(x => x.Summary.CollectionKind is null && x.Ready);
        if (kind != "program" || references.Any(x => x.Summary.CollectionKind != "course" || x.ItemIds.Length == 0)) return false;
        var leaves = references.SelectMany(x => x.ItemIds).Distinct().ToArray();
        var works = await LoadReferencesAsync(leaves, locking, token);
        return works.Length == leaves.Length && works.All(x => x.Summary.CollectionKind is null && x.Ready);
    }
    private static ContentSummary[] Ordered(Guid[] ids, Reference[] references)
    {
        var byId = references.ToDictionary(x => x.Summary.Id, x => x.Summary);
        return ids.Where(byId.ContainsKey).Select(x => byId[x]).ToArray();
    }
    private static bool HasSimpleWork(EditorialContent entry) => entry.CollectionKind is null &&
        ((entry.Category == "lecturas" && entry.WorkText is not null) || (CatalogRules.VideoCategory(entry.Category) && entry.YouTubeId is not null));
    private static void Apply(EditorialContent entry, CreateContentRequest request)
    {
        entry.Slug = request.Slug;
        entry.Title = request.Title.Trim();
        entry.Summary = request.Summary.Trim();
        entry.Body = request.Body.Trim();
        entry.Category = request.Category;
        entry.CoverAsset = request.CoverAsset;
        entry.DurationSeconds = request.DurationSeconds;
        entry.Author = request.Author?.Trim();
        entry.Tags = (request.Tags ?? []).Select(x => x.Trim()).ToArray();
        entry.WorkText = request.WorkText?.Trim();
        entry.YouTubeId = request.YouTubeId;
        entry.CollectionKind = request.CollectionKind;
        entry.ItemIds = (request.ItemIds ?? []).ToArray();
    }
    private static CatalogResult<AdminContentView> InvalidReferences() => CatalogResult<AdminContentView>.Fail("collection_items_invalid", 409,
        new() { ["itemIds"] = ["Los cursos requieren obras publicadas disponibles; los programas requieren cursos publicados disponibles."] });
    private static IQueryable<EditorialContent> Filter(IQueryable<EditorialContent> query, string? search, string? category)
    {
        if (category is not null) query = query.Where(x => x.Category == category);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + CatalogRules.EscapeSearch(search.Trim()) + "%";
            query = query.Where(x => EF.Functions.ILike(x.Title, pattern, "\\") || EF.Functions.ILike(x.Summary, pattern, "\\") || (x.Author != null && EF.Functions.ILike(x.Author, pattern, "\\")));
        }
        return query;
    }
    private static bool IsTransactionConflict(Exception error)
    {
        // Npgsql's non-retrying strategy wraps transient database errors once.
        if (error is InvalidOperationException { InnerException: { } strategyError }) error = strategyError;
        if (error is DbUpdateException { InnerException: { } updateError }) error = updateError;
        return error is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure };
    }
    private static bool IsSlugConflict(DbUpdateException error) => error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Contents_Slug" };
    private static ContentAudit Audit(Guid actorId, EditorialContent entry, string action, object changes, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), ActorId = actorId, ContentId = entry.Id, Action = action, Changes = JsonSerializer.Serialize(changes), CreatedUtc = now };
    private static AdminContentView View(EditorialContent x) => new(x.Id, x.Slug, x.Title, x.Summary, x.Body, x.Category, x.CoverAsset, x.DurationSeconds, x.PublishedUtc, x.Status, x.CreatedUtc, x.UpdatedUtc, x.Version, x.Author, x.Tags.ToArray(), x.WorkText, x.YouTubeId, x.CollectionKind, x.ItemIds.ToArray());
}
