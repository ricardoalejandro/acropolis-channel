using Acropolis.Identity.Application;
using Acropolis.Subscriptions.Application;
using Microsoft.EntityFrameworkCore;

namespace Acropolis.Subscriptions.Infrastructure;

public sealed class SubscriptionService(SubscriptionsDbContext database, IIdentityService accounts, TimeProvider clock) : ISubscriptionService, ISubscriptionAccess
{
    public async Task<SubscriptionAccessScope> GetScopeAsync(Guid userId, CancellationToken token)
    {
        var now = Timestamp(clock.GetUtcNow());
        var plan = await database.Subscriptions.AsNoTracking().Where(x => x.UserId == userId && x.Status == "active" &&
            x.StartsUtc <= now && (x.ExpiresUtc == null || now < x.ExpiresUtc)).Select(x => x.Plan).SingleOrDefaultAsync(token);
        return plan switch
        {
            SubscriptionRules.Plan => SubscriptionAccessScope.FreeOnly,
            SubscriptionRules.ProbationPlan or SubscriptionRules.AnnualPlan => SubscriptionAccessScope.FullCatalog,
            _ => SubscriptionAccessScope.None
        };
    }
    public async Task<bool> HasActiveAsync(Guid userId, CancellationToken token) => await GetScopeAsync(userId, token) != SubscriptionAccessScope.None;
    public async Task<SubscriptionStatusView> GetMineAsync(Guid userId, CancellationToken token)
    {
        var row = await database.Subscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, token);
        var account = await accounts.GetUserAsync(userId, token);
        return new(row is null ? null : View(row), SubscriptionRules.Eligible(Active(account), row?.Status) && (row is null || row.Plan == SubscriptionRules.Plan));
    }
    public async Task<SubscriptionResult<SubscriptionView>> ActivateAsync(Guid userId, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await LockUser(userId, token);
        var account = await accounts.GetUserAsync(userId, token);
        if (!Active(account)) return SubscriptionResult<SubscriptionView>.Fail("account_not_active", 403);
        var row = await database.Subscriptions.SingleOrDefaultAsync(x => x.UserId == userId, token);
        if (row?.Status == "suspended") return SubscriptionResult<SubscriptionView>.Fail("subscription_suspended", 403);
        if (row is not null && row.Plan != SubscriptionRules.Plan) return SubscriptionResult<SubscriptionView>.Fail("plan_change_requires_manager", 409);
        if (row?.Status == "active") return SubscriptionResult<SubscriptionView>.Ok(View(row));
        var now = Timestamp(clock.GetUtcNow());
        var before = row?.Status;
        var beforeTerms = row is null ? null : new Terms(row.Status, row.Plan, row.StartsUtc, row.ExpiresUtc);
        if (row is null)
        {
            row = new Subscription { Id = Guid.NewGuid(), UserId = userId, CreatedUtc = now, StartsUtc = now, ActivatedUtc = now, UpdatedUtc = now };
            database.Subscriptions.Add(row);
        }
        else
        {
            row.Status = "active"; row.StartsUtc = now; row.ActivatedUtc = now; row.UpdatedUtc = now; row.CancelledUtc = null;
            row.Version = Guid.NewGuid().ToString("N");
        }
        Audit(row, userId, before is null ? "subscription.activated" : "subscription.reactivated", before, "Activación gratuita explícita.", beforeTerms);
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
        row.Status = "cancelled"; row.CancelledUtc = Timestamp(clock.GetUtcNow()); row.UpdatedUtc = Timestamp(clock.GetUtcNow()); row.Version = Guid.NewGuid().ToString("N");
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
    public async Task<SubscriptionResult<SubscriptionView>> AssignAsync(Guid actorId, Guid userId, AssignSubscriptionRequest request, CancellationToken token)
    {
        if (!SubscriptionRules.ValidAssignment(request)) return SubscriptionResult<SubscriptionView>.Fail("validation_error");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({IdentityRules.AdministrationLockKey})", token);
        await LockUser(userId, token);
        var actor = await accounts.GetUserAsync(actorId, token);
        if (!Active(actor) || !actor!.Permissions.Contains(IdentityRules.ManageSubscriptions)) return SubscriptionResult<SubscriptionView>.Fail("forbidden", 403);
        var target = await accounts.GetUserAsync(userId, token);
        if (target is null) return SubscriptionResult<SubscriptionView>.Fail("not_found", 404);
        if (target.IsOwner && actorId != target.Id) return SubscriptionResult<SubscriptionView>.Fail("owner_protected", 409);
        if (!Active(target)) return SubscriptionResult<SubscriptionView>.Fail("account_not_active", 403);
        var row = await database.Subscriptions.SingleOrDefaultAsync(x => x.UserId == userId, token);
        if (row is null ? request.Version is not null : row.Version != request.Version)
            return SubscriptionResult<SubscriptionView>.Fail("concurrency_conflict", 409);
        var now = Timestamp(clock.GetUtcNow());
        var startsUtc = request.StartsUtc is { } requestedStart ? Timestamp(requestedStart) : now;
        var expiresUtc = SubscriptionRules.ExpiresFor(request.Plan, startsUtc);
        if (row is not null && row.Status == "active" && row.Plan == request.Plan)
        {
            // Repeating the original assignment must not shorten an already-renewed term.
            if (row.Plan == SubscriptionRules.Plan || row.StartsUtc == startsUtc) return SubscriptionResult<SubscriptionView>.Ok(View(row));
            if (expiresUtc is not null && row.ExpiresUtc == expiresUtc && await database.Audit.AsNoTracking().AnyAsync(x =>
                x.SubscriptionId == row.Id && x.Action == "subscription.renewed" && x.BeforeExpiresUtc == startsUtc &&
                x.AfterExpiresUtc == row.ExpiresUtc && x.AfterStartsUtc == row.StartsUtc && x.AfterPlan == row.Plan, token))
                return SubscriptionResult<SubscriptionView>.Ok(View(row));
        }
        var renewal = row is not null && row.Plan == request.Plan && row.ExpiresUtc is not null && row.ExpiresUtc == startsUtc;
        if (row is not null && SubscriptionRules.EffectiveState(row.Status, row.StartsUtc, row.ExpiresUtc, now) == "active" &&
            startsUtc > now && !renewal)
            return SubscriptionResult<SubscriptionView>.Fail("active_period_would_be_replaced", 409);
        var before = row is null ? null : new Terms(row.Status, row.Plan, row.StartsUtc, row.ExpiresUtc);
        if (row is null)
        {
            row = new Subscription { Id = Guid.NewGuid(), UserId = userId, CreatedUtc = now };
            database.Subscriptions.Add(row);
        }
        row.Plan = request.Plan; row.StartsUtc = renewal ? row.StartsUtc : startsUtc; row.ExpiresUtc = expiresUtc;
        row.Status = "active"; row.ActivatedUtc = now; row.UpdatedUtc = now; row.CancelledUtc = null;
        row.Version = Guid.NewGuid().ToString("N");
        Audit(row, actorId, renewal ? "subscription.renewed" : "subscription.assigned", before?.Status, request.Reason.Trim(), before);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return SubscriptionResult<SubscriptionView>.Ok(View(row));
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
        row.Status = request.Status; row.UpdatedUtc = Timestamp(clock.GetUtcNow()); row.Version = Guid.NewGuid().ToString("N");
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
            .Select(x => new SubscriptionAuditView(x.Id, x.SubscriptionId, x.UserId, x.ActorId, x.Action, x.BeforeStatus, x.AfterStatus, x.Reason, x.CreatedUtc, x.BeforePlan, x.AfterPlan, x.BeforeStartsUtc, x.AfterStartsUtc, x.BeforeExpiresUtc, x.AfterExpiresUtc)).ToArrayAsync(token);
        return new(rows, total, page, pageSize);
    }
    private Task LockUser(Guid userId, CancellationToken token) => database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({IdentityRules.UserLock(userId)})", token);
    private static bool Active(UserView? account) => account is { EmailConfirmed: true, Status: "active" };
    // PostgreSQL timestamps store microseconds; return the exact precision persisted for renewal comparisons.
    private static DateTimeOffset Timestamp(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
    private sealed record Terms(string Status, string Plan, DateTimeOffset StartsUtc, DateTimeOffset? ExpiresUtc);
    private void Audit(Subscription row, Guid actorId, string action, string? beforeStatus, string reason, Terms? before = null) => database.Audit.Add(new SubscriptionAudit
    {
        Id = Guid.NewGuid(),
        SubscriptionId = row.Id,
        UserId = row.UserId,
        ActorId = actorId,
        Action = action,
        BeforeStatus = beforeStatus,
        AfterStatus = row.Status,
        Reason = reason,
        CreatedUtc = Timestamp(clock.GetUtcNow()),
        BeforePlan = before?.Plan ?? (beforeStatus is null ? null : row.Plan),
        AfterPlan = row.Plan,
        BeforeStartsUtc = before?.StartsUtc ?? (beforeStatus is null ? null : row.StartsUtc),
        AfterStartsUtc = row.StartsUtc,
        BeforeExpiresUtc = before is null ? (beforeStatus is null ? null : row.ExpiresUtc) : before.ExpiresUtc,
        AfterExpiresUtc = row.ExpiresUtc
    });
    private SubscriptionView View(Subscription row) => new(row.Id, row.UserId, row.Plan, row.Status, row.CreatedUtc, row.ActivatedUtc, row.UpdatedUtc, row.CancelledUtc, row.ExpiresUtc, row.Version, row.StartsUtc, SubscriptionRules.EffectiveState(row.Status, row.StartsUtc, row.ExpiresUtc, Timestamp(clock.GetUtcNow())));
}
