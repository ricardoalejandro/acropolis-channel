using Acropolis.Catalog.Application;
using Microsoft.EntityFrameworkCore;

namespace Acropolis.Catalog.Infrastructure;

public sealed class ConsumptionRetentionService(CatalogDbContext database, TimeProvider clock) : IConsumptionRetentionService
{
    public async Task<ConsumptionPruneResult> PruneAsync(CancellationToken token)
    {
        var now = ConsumptionActivityRules.ServerUtc(clock.GetUtcNow());
        var receiptCutoff = now.AddHours(-8);
        var detailCutoff = DateOnly.FromDateTime(ConsumptionActivityRules.DetailAvailableFrom(now).UtcDateTime);
        var generalCutoff = DateOnly.FromDateTime(ConsumptionActivityRules.GeneralAvailableFrom(now).UtcDateTime);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        // Deleting receipts first avoids a cascade of thousands of pulses per session.
        // Lock the mutable parent: receipts intentionally lack UPDATE privilege, and parallel
        // cleanup/recording must not process the same session until this transaction completes.
        var pulses = await database.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (
              SELECT p."SessionId",p."Sequence" FROM catalog."ConsumptionPulses" p
              JOIN catalog."ConsumptionSessions" s ON s."Id"=p."SessionId" WHERE s."StartedUtc"<={receiptCutoff}
              ORDER BY s."StartedUtc",p."SessionId",p."Sequence" LIMIT 1000 FOR UPDATE OF s SKIP LOCKED
            ) DELETE FROM catalog."ConsumptionPulses" p USING expired e WHERE p."SessionId"=e."SessionId" AND p."Sequence"=e."Sequence"
            """, token);
        var sessions = await database.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (
              SELECT "Id" FROM catalog."ConsumptionSessions" s WHERE "StartedUtc"<={receiptCutoff}
              AND NOT EXISTS (SELECT 1 FROM catalog."ConsumptionPulses" p WHERE p."SessionId"=s."Id")
              ORDER BY "StartedUtc","Id" LIMIT 1000 FOR UPDATE SKIP LOCKED
            ) DELETE FROM catalog."ConsumptionSessions" s USING expired e WHERE s."Id"=e."Id"
            """, token);
        var accounts = await database.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (
              SELECT "AccountId","DayUtc","ContentId","ContentVersion","CategoryAtStart","SourceKind" FROM catalog."ConsumptionAccountDaily"
              WHERE "DayUtc"<{detailCutoff} ORDER BY "DayUtc","AccountId","ContentId","ContentVersion","CategoryAtStart","SourceKind" LIMIT 1000 FOR UPDATE SKIP LOCKED
            ) DELETE FROM catalog."ConsumptionAccountDaily" d USING expired e WHERE d."AccountId"=e."AccountId" AND d."DayUtc"=e."DayUtc" AND d."ContentId"=e."ContentId"
              AND d."ContentVersion"=e."ContentVersion" AND d."CategoryAtStart"=e."CategoryAtStart" AND d."SourceKind"=e."SourceKind"
            """, token);
        var general = await database.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (
              SELECT "DayUtc","ContentId","ContentVersion","CategoryAtStart","SourceKind" FROM catalog."ConsumptionDaily"
              WHERE "DayUtc"<{generalCutoff} ORDER BY "DayUtc","ContentId","ContentVersion","CategoryAtStart","SourceKind" LIMIT 1000 FOR UPDATE SKIP LOCKED
            ) DELETE FROM catalog."ConsumptionDaily" d USING expired e WHERE d."DayUtc"=e."DayUtc" AND d."ContentId"=e."ContentId"
              AND d."ContentVersion"=e."ContentVersion" AND d."CategoryAtStart"=e."CategoryAtStart" AND d."SourceKind"=e."SourceKind"
            """, token);
        // Account/general aggregates already exist from atomic recording; pruning never recreates them.
        await transaction.CommitAsync(token);
        return new(pulses, sessions, accounts, general);
    }
}
