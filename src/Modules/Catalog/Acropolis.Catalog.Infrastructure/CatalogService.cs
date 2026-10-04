using System.Text.Json;
using Acropolis.Catalog.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Acropolis.Catalog.Infrastructure;

public sealed class CatalogService(CatalogDbContext database, TimeProvider clock) : ICatalogService
{
    public async Task<ContentPage> ListPublishedAsync(string? search, string? category, int page, int pageSize, CancellationToken token)
    {
        var query = Filter(database.Contents.AsNoTracking().Where(x => x.Status == "published"), search, category);
        var total = await query.CountAsync(token);
        var entries = await query.OrderByDescending(x => x.PublishedUtc).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new ContentSummary(x.Id, x.Slug, x.Title, x.Summary, x.Category, x.CoverAsset, x.DurationSeconds, x.PublishedUtc)).ToArrayAsync(token);
        return new(entries, total, page, pageSize);
    }
    public async Task<ContentDetail?> GetPublishedAsync(string slug, CancellationToken token) =>
        await database.Contents.AsNoTracking().Where(x => x.Slug == slug && x.Status == "published")
            .Select(x => new ContentDetail(x.Id, x.Slug, x.Title, x.Summary, x.Body, x.Category, x.CoverAsset, x.DurationSeconds, x.PublishedUtc, x.UpdatedUtc)).SingleOrDefaultAsync(token);
    public async Task<AdminContentPage> ListAdminAsync(string? search, string? category, string? status, int page, int pageSize, CancellationToken token)
    {
        var query = Filter(database.Contents.AsNoTracking(), search, category);
        if (status is not null) query = query.Where(x => x.Status == status);
        var total = await query.CountAsync(token);
        var entries = await query.OrderByDescending(x => x.UpdatedUtc).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new AdminContentSummary(x.Id, x.Slug, x.Title, x.Summary, x.Category, x.CoverAsset, x.DurationSeconds, x.PublishedUtc, x.Status, x.CreatedUtc, x.UpdatedUtc, x.Version)).ToArrayAsync(token);
        return new(entries, total, page, pageSize);
    }
    public async Task<AdminContentView?> GetAdminAsync(Guid id, CancellationToken token)
    {
        var entry = await database.Contents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        return entry is null ? null : View(entry);
    }
    public async Task<CatalogResult<AdminContentView>> CreateAsync(Guid actorId, CreateContentRequest request, CancellationToken token)
    {
        var errors = CatalogRules.Validate(request);
        if (errors.Count > 0) return CatalogResult<AdminContentView>.Fail("validation_error", 400, errors);
        var now = clock.GetUtcNow();
        var entry = new EditorialContent { Id = Guid.NewGuid(), Slug = request.Slug, Title = request.Title.Trim(), Summary = request.Summary.Trim(), Body = request.Body.Trim(), Category = request.Category, CoverAsset = request.CoverAsset, DurationSeconds = request.DurationSeconds, CreatedUtc = now, UpdatedUtc = now };
        database.Contents.Add(entry);
        database.Audit.Add(Audit(actorId, entry, "content.created", new { status = "draft" }, now));
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateException error) when (IsSlugConflict(error)) { return CatalogResult<AdminContentView>.Fail("slug_conflict", 409); }
        return new(View(entry), Status: 201);
    }
    public async Task<CatalogResult<AdminContentView>> UpdateAsync(Guid actorId, Guid id, UpdateContentRequest request, CancellationToken token)
    {
        var errors = CatalogRules.Validate(request);
        if (errors.Count > 0) return CatalogResult<AdminContentView>.Fail("validation_error", 400, errors);
        var entry = await database.Contents.SingleOrDefaultAsync(x => x.Id == id, token);
        if (entry is null) return CatalogResult<AdminContentView>.Fail("not_found", 404);
        if (entry.Version != request.Version) return CatalogResult<AdminContentView>.Fail("concurrency_conflict", 409);
        if (!CatalogRules.CanTransition(entry.Status, request.Status)) return CatalogResult<AdminContentView>.Fail("invalid_transition", 409);
        if (entry.PublishedUtc is not null && entry.Slug != request.Slug) return CatalogResult<AdminContentView>.Fail("slug_immutable", 409,
            new() { ["slug"] = ["El enlace permanece estable después de la primera publicación."] });
        var before = new { status = entry.Status, titleChanged = entry.Title != request.Title.Trim(), synopsisChanged = entry.Body != request.Body.Trim() || entry.Summary != request.Summary.Trim() };
        var now = clock.GetUtcNow();
        entry.Slug = request.Slug;
        entry.Title = request.Title.Trim();
        entry.Summary = request.Summary.Trim();
        entry.Body = request.Body.Trim();
        entry.Category = request.Category;
        entry.CoverAsset = request.CoverAsset;
        entry.DurationSeconds = request.DurationSeconds;
        entry.Status = request.Status;
        entry.UpdatedUtc = now;
        if (entry.Status == "published" && entry.PublishedUtc is null) entry.PublishedUtc = now;
        entry.Version = Guid.NewGuid().ToString("N");
        database.Audit.Add(Audit(actorId, entry, "content.updated", new { before, status = entry.Status }, now));
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return CatalogResult<AdminContentView>.Fail("concurrency_conflict", 409); }
        catch (DbUpdateException error) when (IsSlugConflict(error)) { return CatalogResult<AdminContentView>.Fail("slug_conflict", 409); }
        return new(View(entry));
    }
    private static IQueryable<EditorialContent> Filter(IQueryable<EditorialContent> query, string? search, string? category)
    {
        if (category is not null) query = query.Where(x => x.Category == category);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + CatalogRules.EscapeSearch(search.Trim()) + "%";
            query = query.Where(x => EF.Functions.ILike(x.Title, pattern, "\\") || EF.Functions.ILike(x.Summary, pattern, "\\"));
        }
        return query;
    }
    private static bool IsSlugConflict(DbUpdateException error) => error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Contents_Slug" };
    private static ContentAudit Audit(Guid actorId, EditorialContent entry, string action, object changes, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), ActorId = actorId, ContentId = entry.Id, Action = action, Changes = JsonSerializer.Serialize(changes), CreatedUtc = now };
    private static AdminContentView View(EditorialContent x) => new(x.Id, x.Slug, x.Title, x.Summary, x.Body, x.Category, x.CoverAsset, x.DurationSeconds, x.PublishedUtc, x.Status, x.CreatedUtc, x.UpdatedUtc, x.Version);
}
