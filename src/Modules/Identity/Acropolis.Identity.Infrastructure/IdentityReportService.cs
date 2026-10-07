using System.Data;
using Acropolis.Identity.Application;
using Microsoft.EntityFrameworkCore;

namespace Acropolis.Identity.Infrastructure;

public sealed class IdentityReportService(IdentityDbContext database, TimeProvider clock) : IIdentityReportService
{
    private static readonly string[] Statuses = ["active", "pending", "disabled"];

    public Task<IdentityReportView> GetCurrentAsync(CancellationToken token) =>
        database.Database.CreateExecutionStrategy().ExecuteAsync(async operationToken =>
        {
            // Both aggregates observe one read-only snapshot, even when accounts change concurrently.
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, operationToken);
            await database.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", operationToken);
            var generatedUtc = clock.GetUtcNow().ToUniversalTime();
            var accounts = await database.Users.AsNoTracking()
                .GroupBy(x => new { x.EmailConfirmed, x.IsDisabled, x.RevalidationRequired })
                .Select(x => new { x.Key.EmailConfirmed, x.Key.IsDisabled, x.Key.RevalidationRequired, Count = x.LongCount() })
                .ToArrayAsync(operationToken);
            var levels = await database.Set<UserLevel>().AsNoTracking().GroupBy(x => x.Level)
                .Select(x => new { Level = x.Key, Count = x.LongCount() }).ToArrayAsync(operationToken);
            var statusCounts = accounts.GroupBy(x => IdentityRules.Status(x.EmailConfirmed, x.IsDisabled, x.RevalidationRequired))
                .ToDictionary(x => x.Key, x => x.Sum(value => value.Count), StringComparer.Ordinal);
            var levelCounts = levels.ToDictionary(x => x.Level, x => x.Count, StringComparer.Ordinal);
            var result = new IdentityReportView(generatedUtc, accounts.Sum(x => x.Count),
                Statuses.Select(x => new IdentityReportCount(x, statusCounts.GetValueOrDefault(x))).ToArray(),
                IdentityRules.Levels.Select(x => new IdentityReportCount(x, levelCounts.GetValueOrDefault(x))).ToArray());
            await transaction.CommitAsync(operationToken);
            return result;
        }, token);
}
