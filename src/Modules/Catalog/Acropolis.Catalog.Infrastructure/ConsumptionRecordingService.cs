using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Acropolis.Catalog.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Acropolis.Catalog.Infrastructure;

public sealed class ConsumptionRecordingService(CatalogDbContext database, TimeProvider clock, IOptions<ConsumptionRecordingOptions> options) : IConsumptionRecordingService
{
    public ConsumptionCapabilitiesView GetCapabilities() => new(options.Value.RecordingEnabled, ConsumptionActivityRules.DetailRetentionDays, ConsumptionActivityRules.GeneralRetentionDays);
    public async Task<CatalogResult<ConsumptionSessionView>> StartAsync(Guid accountId, string authenticationBindingHash, string slug, StartConsumptionRequest request, bool allowRestricted, CancellationToken token)
    {
        if (!options.Value.RecordingEnabled) return CatalogResult<ConsumptionSessionView>.Fail("recording_disabled", 503);
        if (accountId == Guid.Empty || !ConsumptionActivityRules.ValidBinding(authenticationBindingHash) || !CatalogRules.ValidSlug(slug) || !ConsumptionActivityRules.Valid(request))
            return CatalogResult<ConsumptionSessionView>.Fail("validation_error", 400);
        try { return await StartCoreAsync(accountId, authenticationBindingHash, slug, request, allowRestricted, token); }
        catch (Exception error) when (IsTransactionConflict(error)) { return CatalogResult<ConsumptionSessionView>.Fail("concurrency_conflict", 409); }
        finally { database.ChangeTracker.Clear(); }
    }
    private async Task<CatalogResult<ConsumptionSessionView>> StartCoreAsync(Guid accountId, string binding, string slug, StartConsumptionRequest request, bool allowRestricted, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var lockKey = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"consumption:{accountId:N}:{request.VisitId:N}")));
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", token);
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM catalog.\"Contents\" WHERE \"Slug\"={slug} FOR SHARE", token);
        var content = await database.Contents.AsNoTracking().SingleOrDefaultAsync(x => x.Slug == slug && x.Status == "published", token);
        var kind = Kind(content);
        if (content is null || kind is null) return CatalogResult<ConsumptionSessionView>.Fail("not_found", 404);
        if (!allowRestricted && !content.IsFree) return CatalogResult<ConsumptionSessionView>.Fail("content_requires_plan", 403);
        if (content.Version != request.ContentVersion) return CatalogResult<ConsumptionSessionView>.Fail("content_changed", 409);
        var now = ConsumptionActivityRules.ServerUtc(clock.GetUtcNow());
        var existing = await database.ConsumptionSessions.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId && x.VisitId == request.VisitId, token);
        if (existing is not null)
        {
            if (existing.AuthenticationBindingHash != binding || existing.ContentId != content.Id || existing.ContentVersion != request.ContentVersion || now - existing.StartedUtc >= TimeSpan.FromHours(8) || existing.LastReceivedUtc < now.AddDays(-ConsumptionActivityRules.DetailRetentionDays))
                return CatalogResult<ConsumptionSessionView>.Fail("concurrency_conflict", 409);
            return new(new(existing.Id, existing.StartedUtc, existing.LastSequence + 1, existing.SourceKind));
        }
        var session = new ConsumptionSession { Id = Guid.NewGuid(), AccountId = accountId, AuthenticationBindingHash = binding, VisitId = request.VisitId, ContentId = content.Id, ContentVersion = content.Version, CategoryAtStart = content.Category, SourceKind = kind, StartedUtc = now, LastReceivedUtc = now };
        database.ConsumptionSessions.Add(session);
        await database.SaveChangesAsync(token);
        await IncrementDailyAsync(session, now, 1, null, token);
        await transaction.CommitAsync(token);
        return new(new(session.Id, session.StartedUtc, 1, kind));
    }
    public async Task<CatalogResult<ConsumptionPulseReceipt>> PulseAsync(Guid accountId, string authenticationBindingHash, Guid sessionId, ConsumptionPulseRequest request, bool allowRestricted, CancellationToken token)
    {
        if (!options.Value.RecordingEnabled) return CatalogResult<ConsumptionPulseReceipt>.Fail("recording_disabled", 503);
        if (accountId == Guid.Empty || sessionId == Guid.Empty || !ConsumptionActivityRules.ValidBinding(authenticationBindingHash)) return CatalogResult<ConsumptionPulseReceipt>.Fail("validation_error", 400);
        try { return await PulseCoreAsync(accountId, authenticationBindingHash, sessionId, request, allowRestricted, token); }
        catch (Exception error) when (IsTransactionConflict(error)) { return CatalogResult<ConsumptionPulseReceipt>.Fail("concurrency_conflict", 409); }
        finally { database.ChangeTracker.Clear(); }
    }
    private async Task<CatalogResult<ConsumptionPulseReceipt>> PulseCoreAsync(Guid accountId, string binding, Guid id, ConsumptionPulseRequest request, bool allowRestricted, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var session = await database.ConsumptionSessions.FromSqlInterpolated($"SELECT * FROM catalog.\"ConsumptionSessions\" WHERE \"Id\"={id} AND \"AccountId\"={accountId} AND \"AuthenticationBindingHash\"={binding} FOR UPDATE").SingleOrDefaultAsync(token);
        var now = ConsumptionActivityRules.ServerUtc(clock.GetUtcNow());
        if (session is null || session.LastReceivedUtc < now.AddDays(-ConsumptionActivityRules.DetailRetentionDays) || now - session.StartedUtc >= TimeSpan.FromHours(8))
            return CatalogResult<ConsumptionPulseReceipt>.Fail("not_found", 404);
        if (!ConsumptionActivityRules.Valid(request, session.SourceKind)) return CatalogResult<ConsumptionPulseReceipt>.Fail("validation_error", 400);
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM catalog.\"Contents\" WHERE \"Id\"={session.ContentId} FOR SHARE", token);
        var content = await database.Contents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == session.ContentId && x.Status == "published", token);
        if (Kind(content) != session.SourceKind) return CatalogResult<ConsumptionPulseReceipt>.Fail("not_found", 404);
        if (!allowRestricted && !content!.IsFree) return CatalogResult<ConsumptionPulseReceipt>.Fail("content_requires_plan", 403);
        if (content!.Version != session.ContentVersion) return CatalogResult<ConsumptionPulseReceipt>.Fail("content_changed", 409);
        var hash = ConsumptionActivityRules.CanonicalHash(request);
        var existing = await database.ConsumptionPulses.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == id && x.Sequence == request.Sequence, token);
        if (existing is not null)
            return existing.CanonicalHash == hash ? new(Receipt(existing)) : CatalogResult<ConsumptionPulseReceipt>.Fail("concurrency_conflict", 409);
        if (request.Sequence != session.LastSequence + 1) return CatalogResult<ConsumptionPulseReceipt>.Fail("concurrency_conflict", 409);
        var elapsed = (int)Math.Clamp((now - session.LastReceivedUtc).TotalMilliseconds, 0, ConsumptionActivityRules.MaximumPulseMs);
        var state = new ConsumptionProgressState(JsonSerializer.Deserialize<ConsumptionRange[]>(session.CoverageJson) ?? [], session.DurationMs, session.DurationChanged, session.CoverageIncomplete, session.EndedReported);
        ConsumptionProgressUpdate observed;
        try { observed = ConsumptionActivityProgress.Observe(state, request, session.SourceKind, elapsed); }
        catch (ArgumentException) { return CatalogResult<ConsumptionPulseReceipt>.Fail("validation_error", 400); }
        // Never move the receipt clock backwards after a system clock correction.
        now = now < session.LastReceivedUtc ? session.LastReceivedUtc : now;
        var pulse = new ConsumptionPulse { SessionId = id, Sequence = request.Sequence, CanonicalHash = hash, ReceivedUtc = now, CreditedMs = observed.CreditedMs, ProgressBasisPoints = observed.ProgressBasisPoints, CoverageIncomplete = observed.State.CoverageIncomplete, EndedReported = observed.State.EndedReported, EndedNow = observed.EndedNow };
        session.LastSequence = request.Sequence; session.LastReceivedUtc = now;
        session.CoverageJson = JsonSerializer.Serialize(observed.State.Coverage); session.DurationMs = observed.State.DurationMs;
        session.DurationChanged = observed.State.DurationChanged; session.CoverageIncomplete = observed.State.CoverageIncomplete; session.EndedReported = observed.State.EndedReported;
        database.ConsumptionPulses.Add(pulse);
        await database.SaveChangesAsync(token);
        await IncrementDailyAsync(session, now, 0, pulse, token);
        await transaction.CommitAsync(token);
        return new(Receipt(pulse));
    }
    private async Task IncrementDailyAsync(ConsumptionSession session, DateTimeOffset now, long starts, ConsumptionPulse? pulse, CancellationToken token)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        long recorded = pulse is null ? 0 : 1, credit = pulse?.CreditedMs ?? 0, ended = pulse?.EndedNow == true ? 1 : 0;
        long known = pulse?.ProgressBasisPoints is not null ? 1 : 0, unknown = pulse is not null && pulse.ProgressBasisPoints is null ? 1 : 0, progress = pulse?.ProgressBasisPoints ?? 0;
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO catalog."ConsumptionDaily" ("DayUtc","ContentId","ContentVersion","CategoryAtStart","SourceKind","Starts","RecordedPulses","CreditedMs","EndedReports","KnownProgressSamples","UnknownProgressSamples","ProgressBasisPointsSum")
            VALUES ({day},{session.ContentId},{session.ContentVersion},{session.CategoryAtStart},{session.SourceKind},{starts},{recorded},{credit},{ended},{known},{unknown},{progress})
            ON CONFLICT ("DayUtc","ContentId","ContentVersion","CategoryAtStart","SourceKind") DO UPDATE SET
              "Starts"=catalog."ConsumptionDaily"."Starts"+EXCLUDED."Starts",
              "RecordedPulses"=catalog."ConsumptionDaily"."RecordedPulses"+EXCLUDED."RecordedPulses",
              "CreditedMs"=catalog."ConsumptionDaily"."CreditedMs"+EXCLUDED."CreditedMs",
              "EndedReports"=catalog."ConsumptionDaily"."EndedReports"+EXCLUDED."EndedReports",
              "KnownProgressSamples"=catalog."ConsumptionDaily"."KnownProgressSamples"+EXCLUDED."KnownProgressSamples",
              "UnknownProgressSamples"=catalog."ConsumptionDaily"."UnknownProgressSamples"+EXCLUDED."UnknownProgressSamples",
              "ProgressBasisPointsSum"=catalog."ConsumptionDaily"."ProgressBasisPointsSum"+EXCLUDED."ProgressBasisPointsSum"
            """, token);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO catalog."ConsumptionAccountDaily" ("AccountId","DayUtc","ContentId","ContentVersion","CategoryAtStart","SourceKind","Starts","RecordedPulses","CreditedMs","EndedReports","KnownProgressSamples","UnknownProgressSamples","ProgressBasisPointsSum")
            VALUES ({session.AccountId},{day},{session.ContentId},{session.ContentVersion},{session.CategoryAtStart},{session.SourceKind},{starts},{recorded},{credit},{ended},{known},{unknown},{progress})
            ON CONFLICT ("AccountId","DayUtc","ContentId","ContentVersion","CategoryAtStart","SourceKind") DO UPDATE SET
              "Starts"=catalog."ConsumptionAccountDaily"."Starts"+EXCLUDED."Starts",
              "RecordedPulses"=catalog."ConsumptionAccountDaily"."RecordedPulses"+EXCLUDED."RecordedPulses",
              "CreditedMs"=catalog."ConsumptionAccountDaily"."CreditedMs"+EXCLUDED."CreditedMs",
              "EndedReports"=catalog."ConsumptionAccountDaily"."EndedReports"+EXCLUDED."EndedReports",
              "KnownProgressSamples"=catalog."ConsumptionAccountDaily"."KnownProgressSamples"+EXCLUDED."KnownProgressSamples",
              "UnknownProgressSamples"=catalog."ConsumptionAccountDaily"."UnknownProgressSamples"+EXCLUDED."UnknownProgressSamples",
              "ProgressBasisPointsSum"=catalog."ConsumptionAccountDaily"."ProgressBasisPointsSum"+EXCLUDED."ProgressBasisPointsSum"
            """, token);
    }
    private static string? Kind(EditorialContent? content) => content is not { CollectionKind: null } ? null : content.Category == "lecturas" && content.WorkText is not null ? "reading" : CatalogRules.VideoCategory(content.Category) && content.YouTubeId is not null ? "youtube" : null;
    private static ConsumptionPulseReceipt Receipt(ConsumptionPulse pulse) => new(pulse.Sequence, pulse.ReceivedUtc, pulse.CreditedMs, pulse.ProgressBasisPoints, pulse.CoverageIncomplete, pulse.EndedReported);
    private static bool IsTransactionConflict(Exception error)
    {
        if (error is InvalidOperationException { InnerException: { } wrapped }) error = wrapped;
        if (error is DbUpdateException { InnerException: { } update }) error = update;
        return error is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure };
    }
}
