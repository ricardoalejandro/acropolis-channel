using System.Data;
using System.Text.Json;
using Acropolis.Catalog.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Acropolis.Catalog.Infrastructure;

public sealed class TopicService(CatalogDbContext database, TimeProvider clock) : ITopicService
{
    public async Task<PublicTopicPage> ListPublicAsync(int page, int pageSize, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var query = database.Topics.AsNoTracking().Where(x => x.Status == "active");
        var total = await query.CountAsync(token);
        var items = await query.OrderBy(x => x.Position).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new PublicTopicView(x.Id, x.Slug, x.Name, x.Position)).ToArrayAsync(token);
        return new(items, total, page, pageSize);
    }
    public async Task<AdminTopicPage> ListAdminAsync(string? search, string? status, int page, int pageSize, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var revision = await database.TopicDirectory.AsNoTracking().SingleAsync(token);
        var query = database.Topics.AsNoTracking();
        if (status is not null) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + CatalogRules.EscapeSearch(search.Trim()) + "%";
            query = query.Where(x => EF.Functions.ILike(x.Name, pattern, "\\"));
        }
        var total = await query.CountAsync(token);
        var items = await query.OrderBy(x => x.Position).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new AdminTopicView(x.Id, x.Slug, x.Name, x.Status, x.Position, x.Version)).ToArrayAsync(token);
        return new(items, total, page, pageSize, revision.Version);
    }
    public async Task<AdminTopicView?> GetAdminAsync(Guid id, CancellationToken token)
    {
        var entry = await database.Topics.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        return entry is null ? null : View(entry);
    }
    public async Task<TopicAuditPage?> ListAuditAsync(Guid id, int page, int pageSize, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        if (!await database.Topics.AnyAsync(x => x.Id == id, token)) return null;
        var query = database.TopicAudit.AsNoTracking().Where(x => x.TopicId == id);
        var total = await query.CountAsync(token);
        var items = await query.OrderByDescending(x => x.CreatedUtc).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new TopicAuditView(x.Id, x.ActorId, x.TopicId, x.Action, x.Changes, x.CreatedUtc)).ToArrayAsync(token);
        return new(items, total, page, pageSize);
    }
    public async Task<ContentPage> ListPublishedByTopicAsync(string topic, string? search, string? category, int page, int pageSize, CancellationToken token)
    {
        var query = database.Contents.AsNoTracking().Where(x => x.Status == "published" &&
            database.ContentTopics.Any(link => link.ContentId == x.Id && database.Topics.Any(t => t.Id == link.TopicId && t.Slug == topic && t.Status == "active")));
        if (category is not null) query = query.Where(x => x.Category == category);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + CatalogRules.EscapeSearch(search.Trim()) + "%";
            query = query.Where(x => EF.Functions.ILike(x.Title, pattern, "\\") || EF.Functions.ILike(x.Summary, pattern, "\\") || (x.Author != null && EF.Functions.ILike(x.Author, pattern, "\\")));
        }
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var total = await query.CountAsync(token);
        var items = await query.OrderByDescending(x => x.PublishedUtc).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new ContentSummary(x.Id, x.Slug, x.Title, x.Summary, x.Category, x.CoverAsset, x.DurationSeconds, x.PublishedUtc, x.Author, x.Tags, x.CollectionKind)).ToArrayAsync(token);
        return new(items, total, page, pageSize);
    }
    public async Task<ContentTopicsView?> GetContentTopicsAsync(Guid id, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var content = await database.Contents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        return content is null ? null : new(id, content.Version, await AssignedViewsAsync(id, token));
    }
    public Task<CatalogResult<AdminTopicView>> CreateAsync(Guid actorId, CreateTopicRequest request, CancellationToken token)
    {
        var errors = TopicRules.Validate(request);
        if (errors.Count > 0) return Task.FromResult(CatalogResult<AdminTopicView>.Fail("validation_error", 400, errors));
        return WriteAsync<AdminTopicView>(async () =>
        {
            var directory = await LockDirectoryAsync(token);
            var last = await database.Topics.MaxAsync(x => (int?)x.Position, token) ?? -1;
            if (last == int.MaxValue) return CatalogResult<AdminTopicView>.Fail("topic_order_limit", 409);
            var now = clock.GetUtcNow().ToUniversalTime();
            var entry = new CatalogTopic { Id = Guid.NewGuid(), Slug = request.Slug, Name = request.Name.Trim(), Position = last + 1, CreatedUtc = now, UpdatedUtc = now };
            database.Topics.Add(entry);
            Record(actorId, entry, "topic.created", new { fields = new[] { "name", "slug" } }, now);
            Rotate(directory);
            return new(View(entry));
        }, token);
    }
    public Task<CatalogResult<AdminTopicView>> UpdateAsync(Guid actorId, Guid id, UpdateTopicRequest request, CancellationToken token)
    {
        var errors = TopicRules.Validate(request);
        if (errors.Count > 0) return Task.FromResult(CatalogResult<AdminTopicView>.Fail("validation_error", 400, errors));
        return WriteAsync<AdminTopicView>(async () =>
        {
            var directory = await LockDirectoryAsync(token); var entry = await LockTopicAsync(id, token);
            if (entry is null) return CatalogResult<AdminTopicView>.Fail("not_found", 404);
            if (entry.Version != request.Version) return CatalogResult<AdminTopicView>.Fail("concurrency_conflict", 409);
            if (entry.Name == request.Name.Trim()) return new(View(entry));
            entry.Name = request.Name.Trim(); Change(entry); Rotate(directory);
            Record(actorId, entry, "topic.updated", new { fields = new[] { "name" } }, entry.UpdatedUtc);
            return new(View(entry));
        }, token);
    }
    public Task<CatalogResult<AdminTopicView>> SetStateAsync(Guid actorId, Guid id, TopicStateRequest request, CancellationToken token)
    {
        var errors = TopicRules.Validate(request);
        if (errors.Count > 0) return Task.FromResult(CatalogResult<AdminTopicView>.Fail("validation_error", 400, errors));
        return WriteAsync<AdminTopicView>(async () =>
        {
            var directory = await LockDirectoryAsync(token); var entry = await LockTopicAsync(id, token);
            if (entry is null) return CatalogResult<AdminTopicView>.Fail("not_found", 404);
            if (entry.Version != request.Version) return CatalogResult<AdminTopicView>.Fail("concurrency_conflict", 409);
            if (entry.Status == request.Status) return new(View(entry));
            var before = entry.Status; entry.Status = request.Status; Change(entry); Rotate(directory);
            Record(actorId, entry, entry.Status == "archived" ? "topic.archived" : "topic.restored", new { before, after = entry.Status }, entry.UpdatedUtc);
            return new(View(entry));
        }, token);
    }
    public Task<CatalogResult<AdminTopicView>> MoveAsync(Guid actorId, MoveTopicRequest request, CancellationToken token)
    {
        var errors = TopicRules.Validate(request);
        if (errors.Count > 0) return Task.FromResult(CatalogResult<AdminTopicView>.Fail("validation_error", 400, errors));
        return WriteAsync<AdminTopicView>(async () =>
        {
            var directory = await LockDirectoryAsync(token);
            if (directory.Version != request.DirectoryVersion) return CatalogResult<AdminTopicView>.Fail("concurrency_conflict", 409);
            var entry = await LockTopicAsync(request.Id, token);
            if (entry is null) return CatalogResult<AdminTopicView>.Fail("not_found", 404);
            var before = request.BeforeId is Guid anchor ? await database.Topics.AsNoTracking().SingleOrDefaultAsync(x => x.Id == anchor, token) : null;
            if (request.BeforeId is not null && before is null) return CatalogResult<AdminTopicView>.Fail("not_found", 404);
            var oldPosition = entry.Position;
            var target = before is null ? await database.Topics.MaxAsync(x => x.Position, token) : before.Position - (before.Position > oldPosition ? 1 : 0);
            if (target == oldPosition) return new(View(entry));
            var now = clock.GetUtcNow().ToUniversalTime(); var version = Guid.NewGuid().ToString("N");
            // A constant-size command shifts only the affected interval; no client-supplied full directory.
            if (target < oldPosition)
                await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE catalog.\"Topics\" SET \"Position\"=\"Position\"+1, \"Version\"={version}, \"UpdatedUtc\"={now} WHERE \"Position\">={target} AND \"Position\"<{oldPosition}", token);
            else
                await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE catalog.\"Topics\" SET \"Position\"=\"Position\"-1, \"Version\"={version}, \"UpdatedUtc\"={now} WHERE \"Position\">{oldPosition} AND \"Position\"<={target}", token);
            entry.Position = target; Change(entry); Rotate(directory);
            Record(actorId, entry, "topic.moved", new { before = oldPosition, after = target, request.BeforeId }, now);
            return new(View(entry));
        }, token);
    }
    public Task<CatalogResult<ContentTopicsView>> AssignAsync(Guid actorId, Guid id, AssignContentTopicsRequest request, CancellationToken token)
    {
        var errors = TopicRules.Validate(request);
        if (errors.Count > 0) return Task.FromResult(CatalogResult<ContentTopicsView>.Fail("validation_error", 400, errors));
        return WriteAsync<ContentTopicsView>(async () =>
        {
            var trackedContent = database.ChangeTracker.Entries<EditorialContent>().Any(x => x.Entity.Id == id);
            var contents = await database.Contents.FromSqlInterpolated($"SELECT * FROM catalog.\"Contents\" WHERE \"Id\"={id} FOR UPDATE").ToArrayAsync(token);
            var content = contents.SingleOrDefault();
            if (content is null) return CatalogResult<ContentTopicsView>.Fail("not_found", 404);
            if (trackedContent) await database.Entry(content).ReloadAsync(token);
            if (content.Version != request.ContentVersion) return CatalogResult<ContentTopicsView>.Fail("concurrency_conflict", 409);
            var links = await database.ContentTopics.Where(x => x.ContentId == id).ToArrayAsync(token);
            var previous = links.Select(x => x.TopicId).ToHashSet(); var next = request.TopicIds.ToHashSet();
            // Topic locks are sorted and held through commit, so archival cannot race a new association.
            CatalogTopic[] locked = next.Count == 0 ? [] : await database.Topics.FromSqlInterpolated($"SELECT * FROM catalog.\"Topics\" WHERE \"Id\"=ANY({request.TopicIds}) ORDER BY \"Id\" FOR SHARE").AsNoTracking().ToArrayAsync(token);
            if (locked.Length != next.Count || locked.Any(x => x.Status != "active" && !previous.Contains(x.Id)))
                return CatalogResult<ContentTopicsView>.Fail("topic_unavailable", 409, new() { ["topicIds"] = ["Un tema seleccionado ya no está activo. Conservamos tu selección; recarga los temas."] });
            if (previous.SetEquals(next)) return new(new(id, content.Version, locked.OrderBy(x => x.Position).ThenBy(x => x.Id).Select(View).ToArray()));
            database.ContentTopics.RemoveRange(links.Where(x => !next.Contains(x.TopicId)));
            database.ContentTopics.AddRange(next.Except(previous).Select(topicId => new ContentTopic { ContentId = id, TopicId = topicId }));
            content.Version = Guid.NewGuid().ToString("N"); content.UpdatedUtc = clock.GetUtcNow().ToUniversalTime();
            database.Audit.Add(new ContentAudit { Id = Guid.NewGuid(), ActorId = actorId, ContentId = id, Action = "content.topics_updated", Changes = JsonSerializer.Serialize(new { added = next.Except(previous).Order().ToArray(), removed = previous.Except(next).Order().ToArray() }), CreatedUtc = content.UpdatedUtc });
            return new(new(id, content.Version, locked.OrderBy(x => x.Position).ThenBy(x => x.Id).Select(View).ToArray()));
        }, token);
    }
    private async Task<CatalogResult<T>> WriteAsync<T>(Func<Task<CatalogResult<T>>> action, CancellationToken token)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(token);
            var result = await action();
            if (!result.Succeeded) { database.ChangeTracker.Clear(); return result; }
            await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
            return result;
        }
        catch (DbUpdateConcurrencyException) { database.ChangeTracker.Clear(); return CatalogResult<T>.Fail("concurrency_conflict", 409); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Topics_Slug" })
        { database.ChangeTracker.Clear(); return CatalogResult<T>.Fail("topic_slug_conflict", 409); }
        catch (Exception error) when (TransactionConflict(error))
        { database.ChangeTracker.Clear(); return CatalogResult<T>.Fail("concurrency_conflict", 409); }
        catch { database.ChangeTracker.Clear(); throw; }
    }
    private async Task<TopicDirectoryRevision> LockDirectoryAsync(CancellationToken token)
    {
        var tracked = database.ChangeTracker.Entries<TopicDirectoryRevision>().Any();
        var directory = (await database.TopicDirectory.FromSqlRaw("SELECT * FROM catalog.\"TopicDirectory\" WHERE \"Id\"=1 FOR UPDATE").ToArrayAsync(token)).Single();
        if (tracked) await database.Entry(directory).ReloadAsync(token);
        return directory;
    }
    private async Task<CatalogTopic?> LockTopicAsync(Guid id, CancellationToken token)
    {
        var tracked = database.ChangeTracker.Entries<CatalogTopic>().Any(x => x.Entity.Id == id);
        var entry = (await database.Topics.FromSqlInterpolated($"SELECT * FROM catalog.\"Topics\" WHERE \"Id\"={id} FOR UPDATE").ToArrayAsync(token)).SingleOrDefault();
        if (entry is not null && tracked) await database.Entry(entry).ReloadAsync(token);
        return entry;
    }
    private Task<AdminTopicView[]> AssignedViewsAsync(Guid id, CancellationToken token) =>
        (from link in database.ContentTopics.AsNoTracking() join topic in database.Topics.AsNoTracking() on link.TopicId equals topic.Id where link.ContentId == id orderby topic.Position, topic.Id select new AdminTopicView(topic.Id, topic.Slug, topic.Name, topic.Status, topic.Position, topic.Version)).ToArrayAsync(token);
    private void Change(CatalogTopic entry) { entry.Version = Guid.NewGuid().ToString("N"); entry.UpdatedUtc = clock.GetUtcNow().ToUniversalTime(); }
    private static void Rotate(TopicDirectoryRevision directory) => directory.Version = Guid.NewGuid().ToString("N");
    private void Record(Guid actorId, CatalogTopic entry, string action, object changes, DateTimeOffset now) =>
        database.TopicAudit.Add(new TopicAudit { Id = Guid.NewGuid(), ActorId = actorId, TopicId = entry.Id, Action = action, Changes = JsonSerializer.Serialize(changes), CreatedUtc = now });
    private static AdminTopicView View(CatalogTopic x) => new(x.Id, x.Slug, x.Name, x.Status, x.Position, x.Version);
    private static bool TransactionConflict(Exception error)
    {
        if (error is InvalidOperationException { InnerException: { } strategy }) error = strategy;
        if (error is DbUpdateException { InnerException: { } update }) error = update;
        return error is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure };
    }
}
