using Acropolis.Identity.Application;
using Acropolis.Subscriptions.Application;
using Microsoft.EntityFrameworkCore;

namespace Acropolis.Subscriptions.Infrastructure;

public sealed class SubscriptionService(SubscriptionsDbContext database, IIdentityService accounts, TimeProvider clock) : ISubscriptionService, ISubscriptionAccess
{
    public Task<bool> HasActiveAsync(Guid userId, CancellationToken token) => database.Subscriptions.AsNoTracking().AnyAsync(x => x.UserId == userId && x.Status == "active", token);
    public async Task<SubscriptionStatusView> GetMineAsync(Guid userId, CancellationToken token)
    {
        var row = await database.Subscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, token);
        var account = await accounts.GetUserAsync(userId, token);
        return new(row is null ? null : View(row), SubscriptionRules.Eligible(Active(account), row?.Status));
    }
    public async Task<SubscriptionResult<SubscriptionView>> ActivateAsync(Guid userId, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await LockUser(userId, token);
        var account = await accounts.GetUserAsync(userId, token);
        if (!Active(account)) return SubscriptionResult<SubscriptionView>.Fail("account_not_active", 403);
        var row = await database.Subscriptions.SingleOrDefaultAsync(x => x.UserId == userId, token);
        if (row?.Status == "suspended") return SubscriptionResult<SubscriptionView>.Fail("subscription_suspended", 403);
        if (row?.Status == "active") return SubscriptionResult<SubscriptionView>.Ok(View(row));
        var now = clock.GetUtcNow();
        var before = row?.Status;
        if (row is null)
        {
            row = new Subscription { Id = Guid.NewGuid(), UserId = userId, CreatedUtc = now, ActivatedUtc = now, UpdatedUtc = now };
            database.Subscriptions.Add(row);
        }
        else
        {
            row.Status = "active"; row.ActivatedUtc = now; row.UpdatedUtc = now; row.CancelledUtc = null;
            row.Version = Guid.NewGuid().ToString("N");
        }
        Audit(row, userId, before is null ? "subscription.activated" : "subscription.reactivated", before, "Activación gratuita explícita.");
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return SubscriptionResult<SubscriptionView>.Ok(View(row));
    }
    public async Task<SubscriptionResult<SubscriptionView>> CancelAsync(Guid userId, CancelSubscriptionRequest request, CancellationToken token)
    {
        if (!SubscriptionRules.ValidVersion(request.Version)) return SubscriptionResult<SubscriptionView>.Fail("validation_error");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await LockUser(userId, token);
        if (!Active(await accounts.GetUserAsync(userId, token))) return SubscriptionResult<SubscriptionView>.Fail("account_not_active", 403);
        var row = await database.Subscriptions.SingleOrDefaultAsync(x => x.UserId == userId, token);
        if (row is null) return SubscriptionResult<SubscriptionView>.Fail("not_found", 404);
        if (row.Status == "suspended") return SubscriptionResult<SubscriptionView>.Fail("subscription_suspended", 403);
        if (row.Version != request.Version) return SubscriptionResult<SubscriptionView>.Fail("concurrency_conflict", 409);
        if (row.Status == "cancelled") return SubscriptionResult<SubscriptionView>.Ok(View(row));
        var before = row.Status;
        row.Status = "cancelled"; row.CancelledUtc = clock.GetUtcNow(); row.UpdatedUtc = clock.GetUtcNow(); row.Version = Guid.NewGuid().ToString("N");
        Audit(row, userId, "subscription.cancelled", before, "Cancelación solicitada por la cuenta.");
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return SubscriptionResult<SubscriptionView>.Ok(View(row));
    }
    public async Task<SubscriptionPage> ListAsync(string? status, Guid? userId, int page, int pageSize, CancellationToken token)
    {
        var query = database.Subscriptions.AsNoTracking();
        if (status is not null) query = query.Where(x => x.Status == status);
        if (userId is not null) query = query.Where(x => x.UserId == userId.Value);
        var total = await query.CountAsync(token);
        var rows = await query.OrderByDescending(x => x.UpdatedUtc).ThenByDescending(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(token);
        return new(rows.Select(View).ToArray(), total, page, pageSize);
    }
    public async Task<SubscriptionView?> GetAsync(Guid id, CancellationToken token)
    {
        var row = await database.Subscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        return row is null ? null : View(row);
    }
    public async Task<SubscriptionResult<SubscriptionView>> UpdateAsync(Guid actorId, Guid id, AdminSubscriptionRequest request, CancellationToken token)
    {
        if (!SubscriptionRules.ValidAdmin(request)) return SubscriptionResult<SubscriptionView>.Fail("validation_error");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({IdentityRules.AdministrationLockKey})", token);
        var targetUser = await database.Subscriptions.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.UserId).SingleOrDefaultAsync(token);
        if (targetUser is null) return SubscriptionResult<SubscriptionView>.Fail("not_found", 404);
        await LockUser(targetUser.Value, token);
        var actor = await accounts.GetUserAsync(actorId, token);
        if (!Active(actor) || !actor!.Permissions.Contains(IdentityRules.ManageSubscriptions)) return SubscriptionResult<SubscriptionView>.Fail("forbidden", 403);
        var target = await accounts.GetUserAsync(targetUser.Value, token);
        if (target?.IsOwner == true && actorId != target.Id) return SubscriptionResult<SubscriptionView>.Fail("owner_protected", 409);
        if (request.Status == "active" && !Active(target)) return SubscriptionResult<SubscriptionView>.Fail("account_not_active", 403);
        var row = await database.Subscriptions.SingleAsync(x => x.Id == id, token);
        if (row.Version != request.Version) return SubscriptionResult<SubscriptionView>.Fail("concurrency_conflict", 409);
        if (row.Status == request.Status) return SubscriptionResult<SubscriptionView>.Ok(View(row));
        var before = row.Status;
        row.Status = request.Status; row.UpdatedUtc = clock.GetUtcNow(); row.Version = Guid.NewGuid().ToString("N");
        if (row.Status == "active") { row.ActivatedUtc = row.UpdatedUtc; row.CancelledUtc = null; }
        else if (row.Status == "cancelled") row.CancelledUtc = row.UpdatedUtc;
        Audit(row, actorId, "subscription.updated", before, request.Reason.Trim());
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return SubscriptionResult<SubscriptionView>.Ok(View(row));
    }
    public async Task<SubscriptionAuditPage> ListAuditAsync(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? action, Guid? subscriptionId, Guid? userId, int page, int pageSize, CancellationToken token)
    {
        var query = database.Audit.AsNoTracking();
        if (fromUtc is not null) query = query.Where(x => x.CreatedUtc >= fromUtc.Value.ToUniversalTime());
        if (toUtc is not null) query = query.Where(x => x.CreatedUtc <= toUtc.Value.ToUniversalTime());
        if (action is not null) query = query.Where(x => x.Action == action);
        if (subscriptionId is not null) query = query.Where(x => x.SubscriptionId == subscriptionId.Value);
        if (userId is not null) query = query.Where(x => x.UserId == userId.Value);
        var total = await query.CountAsync(token);
        var rows = await query.OrderByDescending(x => x.CreatedUtc).ThenByDescending(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new SubscriptionAuditView(x.Id, x.SubscriptionId, x.UserId, x.ActorId, x.Action, x.BeforeStatus, x.AfterStatus, x.Reason, x.CreatedUtc)).ToArrayAsync(token);
        return new(rows, total, page, pageSize);
    }
    private Task LockUser(Guid userId, CancellationToken token) => database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({IdentityRules.UserLock(userId)})", token);
    private static bool Active(UserView? account) => account is { EmailConfirmed: true, Status: "active" };
    private void Audit(Subscription row, Guid actorId, string action, string? beforeStatus, string reason) => database.Audit.Add(new SubscriptionAudit
    {
        Id = Guid.NewGuid(),
        SubscriptionId = row.Id,
        UserId = row.UserId,
        ActorId = actorId,
        Action = action,
        BeforeStatus = beforeStatus,
        AfterStatus = row.Status,
        Reason = reason,
        CreatedUtc = clock.GetUtcNow()
    });
    private static SubscriptionView View(Subscription row) => new(row.Id, row.UserId, row.Plan, row.Status, row.CreatedUtc, row.ActivatedUtc, row.UpdatedUtc, row.CancelledUtc, null, row.Version);
}
