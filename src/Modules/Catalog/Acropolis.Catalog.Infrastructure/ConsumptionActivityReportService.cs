using System.Data;
using Acropolis.Catalog.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Acropolis.Catalog.Infrastructure;

public sealed class ConsumptionActivityReportService(CatalogDbContext database, TimeProvider clock, IOptions<ConsumptionRecordingOptions> options) : IConsumptionActivityReportService
{
    private sealed record Window(DateTimeOffset Now, DateTimeOffset DetailCutoff, DateTimeOffset GeneralCutoff);
    public async Task<ConsumptionReportView> GetAsync(ConsumptionReportInterval interval, CancellationToken token)
    {
        Validate(interval);
        var window = CurrentWindow();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await database.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
        var rows = await GroupsAsync(interval, window, null, token);
        await transaction.CommitAsync(token);
        return View(interval, window, rows);
    }
    public async Task<ConsumptionContentPage> ListContentAsync(ConsumptionReportInterval interval, int page, int pageSize, CancellationToken token)
    {
        Validate(interval, page, pageSize);
        var window = CurrentWindow();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await database.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
        var (items, total) = await ContentAsync(interval, window, null, page, pageSize, token);
        await transaction.CommitAsync(token);
        return new(window.Now, interval.FromUtc, interval.ToUtc, window.GeneralCutoff, window.DetailCutoff,
            ConsumptionActivityRules.GeneralRetentionDays, options.Value.RecordingEnabled, items, total, page, pageSize);
    }
    public async Task<AccountConsumptionReportView> GetAccountAsync(Guid accountId, ConsumptionReportInterval interval, int page, int pageSize, CancellationToken token)
    {
        Validate(interval, page, pageSize);
        if (accountId == Guid.Empty) throw new ArgumentException("Invalid account.", nameof(accountId));
        var window = CurrentWindow();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await database.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
        var rows = await GroupsAsync(interval, window, accountId, token);
        var (items, total) = await ContentAsync(interval, window, accountId, page, pageSize, token);
        await transaction.CommitAsync(token);
        var summary = Metrics(rows.Single(x => x.GroupType == "summary"));
        return new(window.Now, interval.FromUtc, interval.ToUtc, window.DetailCutoff, ConsumptionActivityRules.DetailRetentionDays,
            options.Value.RecordingEnabled, summary, Days(interval, window, rows), items, total, page, pageSize);
    }
    private Window CurrentWindow()
    {
        var now = ConsumptionActivityRules.ServerUtc(clock.GetUtcNow());
        return new(now, ConsumptionActivityRules.DetailAvailableFrom(now), ConsumptionActivityRules.GeneralAvailableFrom(now));
    }
    private static void Validate(ConsumptionReportInterval interval, int page = 1, int pageSize = 20)
    {
        if (!ConsumptionActivityRules.ValidInterval(interval) || !ConsumptionActivityRules.ValidPage(page, pageSize)) throw new ArgumentException("Invalid consumption report interval or page.");
    }
    private ConsumptionReportView View(ConsumptionReportInterval interval, Window window, ConsumptionAggregateRow[] rows) =>
        new(window.Now, interval.FromUtc, interval.ToUtc, window.GeneralCutoff, window.DetailCutoff,
            ConsumptionActivityRules.GeneralRetentionDays, options.Value.RecordingEnabled,
            Metrics(rows.Single(x => x.GroupType == "summary")),
            ConsumptionActivityRules.SourceKinds.Select(kind => new ConsumptionGroup(kind, Bucket(rows, "kind", kind, interval.FromUtc >= window.DetailCutoff))).ToArray(),
            CatalogRules.Categories.Select(category => new ConsumptionGroup(category.Id, Bucket(rows, "category", category.Id, interval.FromUtc >= window.DetailCutoff))).ToArray(),
            Days(interval, window, rows));
    private static ConsumptionDay[] Days(ConsumptionReportInterval interval, Window window, ConsumptionAggregateRow[] rows) =>
        Enumerable.Range(0, interval.To.DayNumber - interval.From.DayNumber).Select(offset =>
        {
            var day = interval.From.AddDays(offset);
            var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return new ConsumptionDay(day, Bucket(rows, "day", day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), start >= window.DetailCutoff));
        }).ToArray();
    private static ConsumptionMetrics Bucket(ConsumptionAggregateRow[] rows, string type, string key, bool distinctAvailable) =>
        rows.SingleOrDefault(x => x.GroupType == type && x.Key == key) is { } row ? Metrics(row) : new(distinctAvailable ? 0 : null, 0, 0, 0, 0, 0, 0, 0);
    private static ConsumptionMetrics Metrics(ConsumptionAggregateRow row)
    {
        const long maximumSafeInteger = 9007199254740991;
        var values = new[] { row.Starts, row.RecordedPulses, row.CreditedMs, row.EndedReports, row.KnownProgressSamples, row.UnknownProgressSamples, row.ProgressBasisPointsSum, row.AccountsWithActivity ?? 0 };
        if (values.Any(x => x < 0 || x > maximumSafeInteger)) throw new InvalidOperationException("Consumption report exceeds supported numeric bounds.");
        return new(row.AccountsWithActivity, row.Starts, row.RecordedPulses, row.CreditedMs, row.EndedReports, row.KnownProgressSamples, row.UnknownProgressSamples, row.ProgressBasisPointsSum);
    }
    private Task<ConsumptionAggregateRow[]> GroupsAsync(ConsumptionReportInterval interval, Window window, Guid? accountId, CancellationToken token)
    {
        var sql = Ctes(accountId is not null) + """
            , aggregates AS (
              SELECT CASE WHEN GROUPING("DayUtc")=0 THEN 'day' WHEN GROUPING("SourceKind")=0 THEN 'kind'
                WHEN GROUPING("CategoryAtStart")=0 THEN 'category' ELSE 'summary' END AS "GroupType",
                COALESCE(to_char("DayUtc",'YYYY-MM-DD'),"SourceKind","CategoryAtStart",'') AS "Key",
                "DayUtc","SourceKind","CategoryAtStart",NULL::uuid AS "ContentId",NULL::text AS "ContentVersion",
                COALESCE(SUM("Starts"),0)::bigint AS "Starts",COALESCE(SUM("RecordedPulses"),0)::bigint AS "RecordedPulses",
                COALESCE(SUM("CreditedMs"),0)::bigint AS "CreditedMs",COALESCE(SUM("EndedReports"),0)::bigint AS "EndedReports",
                COALESCE(SUM("KnownProgressSamples"),0)::bigint AS "KnownProgressSamples",COALESCE(SUM("UnknownProgressSamples"),0)::bigint AS "UnknownProgressSamples",
                COALESCE(SUM("ProgressBasisPointsSum"),0)::bigint AS "ProgressBasisPointsSum"
              FROM facts GROUP BY GROUPING SETS ((),("SourceKind"),("CategoryAtStart"),("DayUtc"))
            )
            SELECT a.*, CASE WHEN CASE WHEN a."GroupType"='day' THEN a."DayUtc"::timestamp AT TIME ZONE 'UTC' ELSE @from END < @detailCutoff
              THEN NULL::bigint ELSE (SELECT COUNT(DISTINCT p."AccountId") FROM presence p
                WHERE (a."GroupType"!='day' OR p."DayUtc"=a."DayUtc")
                  AND (a."GroupType"!='kind' OR p."SourceKind"=a."SourceKind")
                  AND (a."GroupType"!='category' OR p."CategoryAtStart"=a."CategoryAtStart")) END AS "AccountsWithActivity",
              NULL::text AS "Slug",NULL::text AS "Title",NULL::text AS "Status"
            FROM aggregates a
            """;
        return database.Database.SqlQueryRaw<ConsumptionAggregateRow>(sql, Parameters(interval, window, accountId)).ToArrayAsync(token);
    }
    private async Task<(ConsumptionContentMetrics[] Items, long Total)> ContentAsync(ConsumptionReportInterval interval, Window window, Guid? accountId, int page, int pageSize, CancellationToken token)
    {
        var cte = Ctes(accountId is not null) + """
            , aggregates AS (
              SELECT "ContentId","ContentVersion","CategoryAtStart","SourceKind",
                COALESCE(SUM("Starts"),0)::bigint AS "Starts",COALESCE(SUM("RecordedPulses"),0)::bigint AS "RecordedPulses",
                COALESCE(SUM("CreditedMs"),0)::bigint AS "CreditedMs",COALESCE(SUM("EndedReports"),0)::bigint AS "EndedReports",
                COALESCE(SUM("KnownProgressSamples"),0)::bigint AS "KnownProgressSamples",COALESCE(SUM("UnknownProgressSamples"),0)::bigint AS "UnknownProgressSamples",
                COALESCE(SUM("ProgressBasisPointsSum"),0)::bigint AS "ProgressBasisPointsSum"
              FROM facts GROUP BY "ContentId","ContentVersion","CategoryAtStart","SourceKind"
            )
            """;
        // CTEs and projection are fixed SQL; every request value remains an NpgsqlParameter.
        const string countProjection = "SELECT COUNT(*) AS \"Value\" FROM aggregates";
        var countSql = cte + countProjection;
        var total = await database.Database.SqlQueryRaw<long>(countSql, Parameters(interval, window, accountId)).SingleAsync(token);
        if (total < 0 || total > 9007199254740991) throw new InvalidOperationException("Consumption content total exceeds supported numeric bounds.");
        var sql = cte + """
            SELECT a.*, 'content'::text AS "GroupType",''::text AS "Key",NULL::date AS "DayUtc",
              CASE WHEN @from < @detailCutoff THEN NULL::bigint ELSE (SELECT COUNT(DISTINCT p."AccountId") FROM presence p
                WHERE p."ContentId"=a."ContentId" AND p."ContentVersion"=a."ContentVersion" AND p."CategoryAtStart"=a."CategoryAtStart" AND p."SourceKind"=a."SourceKind") END AS "AccountsWithActivity",
              c."Slug",c."Title",c."Status"
            FROM aggregates a LEFT JOIN catalog."Contents" c ON c."Id"=a."ContentId"
            ORDER BY a."Starts" DESC,a."ContentId",a."ContentVersion",a."CategoryAtStart",a."SourceKind" LIMIT @take OFFSET @skip
            """;
        var parameters = Parameters(interval, window, accountId).Concat(new object[] { new NpgsqlParameter("take", pageSize), new NpgsqlParameter("skip", (page - 1) * pageSize) }).ToArray();
        var rows = await database.Database.SqlQueryRaw<ConsumptionAggregateRow>(sql, parameters).ToArrayAsync(token);
        return (rows.Select(row => new ConsumptionContentMetrics(row.ContentId!.Value, row.ContentVersion!, row.Slug, row.Title, row.Status, row.CategoryAtStart!, row.SourceKind!, Metrics(row))).ToArray(), total);
    }
    private static object[] Parameters(ConsumptionReportInterval interval, Window window, Guid? accountId) =>
    [
        new NpgsqlParameter("from", interval.FromUtc), new NpgsqlParameter("to", interval.ToUtc), new NpgsqlParameter("now", window.Now),
        new NpgsqlParameter("detailCutoff", window.DetailCutoff), new NpgsqlParameter("generalCutoff", DateOnly.FromDateTime(window.GeneralCutoff.UtcDateTime)),
        new NpgsqlParameter("account", NpgsqlDbType.Uuid) { Value = accountId is { } id ? id : DBNull.Value }
    ];
    private static string Ctes(bool account) => "WITH " + (account ? DetailFacts : GeneralFacts) + ", " + Presence;
    private const string GeneralFacts = """
        facts AS (
          SELECT * FROM catalog."ConsumptionDaily" WHERE "DayUtc">=@generalCutoff AND "DayUtc">=(@from AT TIME ZONE 'UTC')::date
            AND "DayUtc"<(@to AT TIME ZONE 'UTC')::date AND "DayUtc"<=(@now AT TIME ZONE 'UTC')::date
        )
        """;
    private const string DetailFacts = """
        facts AS (
          SELECT "DayUtc","ContentId","ContentVersion","CategoryAtStart","SourceKind","Starts","RecordedPulses","CreditedMs","EndedReports","KnownProgressSamples","UnknownProgressSamples","ProgressBasisPointsSum"
          FROM catalog."ConsumptionAccountDaily" WHERE "AccountId"=@account AND "DayUtc">=(@detailCutoff AT TIME ZONE 'UTC')::date
            AND "DayUtc">=(@from AT TIME ZONE 'UTC')::date AND "DayUtc"<(@to AT TIME ZONE 'UTC')::date AND "DayUtc"<=(@now AT TIME ZONE 'UTC')::date
        )
        """;
    private const string Presence = """
        presence AS (
          SELECT "AccountId","ContentId","ContentVersion","CategoryAtStart","SourceKind","DayUtc"
          FROM catalog."ConsumptionAccountDaily" WHERE (@account IS NULL OR "AccountId"=@account) AND "DayUtc">=(@detailCutoff AT TIME ZONE 'UTC')::date
            AND "DayUtc">=(@from AT TIME ZONE 'UTC')::date AND "DayUtc"<(@to AT TIME ZONE 'UTC')::date AND "DayUtc"<=(@now AT TIME ZONE 'UTC')::date
            AND ("Starts">0 OR "RecordedPulses">0)
        )
        """;
}
public sealed class ConsumptionAggregateRow
{
    public string GroupType { get; set; } = "";
    public string Key { get; set; } = "";
    public DateOnly? DayUtc { get; set; }
    public Guid? ContentId { get; set; }
    public string? ContentVersion { get; set; }
    public string? CategoryAtStart { get; set; }
    public string? SourceKind { get; set; }
    public string? Slug { get; set; }
    public string? Title { get; set; }
    public string? Status { get; set; }
    public long? AccountsWithActivity { get; set; }
    public long Starts { get; set; }
    public long RecordedPulses { get; set; }
    public long CreditedMs { get; set; }
    public long EndedReports { get; set; }
    public long KnownProgressSamples { get; set; }
    public long UnknownProgressSamples { get; set; }
    public long ProgressBasisPointsSum { get; set; }
}
