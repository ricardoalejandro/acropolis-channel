namespace Acropolis.Catalog.Application;

public sealed record ConsumptionCapabilitiesView(bool RecordingEnabled, int DetailRetentionDays, int GeneralRetentionDays);
public sealed record StartConsumptionRequest(Guid VisitId, string ContentVersion);
public sealed record ConsumptionRange(int From, int To);
public sealed record MediaConsumptionObservation(string State, int PositionMs, int? DurationMs, int PlaybackRateMilli, ConsumptionRange[] Segments);
public sealed record ReadingConsumptionObservation(int PositionBasisPoints, ConsumptionRange[] ExposedRanges);
public sealed record ConsumptionPulseRequest(int Sequence, int IntervalMs, int ActiveMs, MediaConsumptionObservation? Media = null, ReadingConsumptionObservation? Reading = null);
public sealed record ConsumptionSessionView(Guid SessionId, DateTimeOffset StartedUtc, int NextSequence, string SourceKind);
public sealed record ConsumptionPulseReceipt(int Sequence, DateTimeOffset RecordedUtc, int CreditedMs, int? ProgressBasisPoints, bool CoverageIncomplete, bool EndedReported);
public sealed record ConsumptionReportInterval(DateOnly From, DateOnly To)
{
    public DateTimeOffset FromUtc => new(From.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    public DateTimeOffset ToUtc => new(To.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
// Counts describe accepted observations. CreditedMs includes concurrent sessions, not human attention.
// Progress mean uses its stored numerator/denominator. Distinct accounts are available only within90 days;
// they are not additive across days/content/categories and are null when the requested interval exceeds that window.
public sealed record ConsumptionMetrics(long? AccountsWithActivity, long Starts, long RecordedPulses, long CreditedMs, long EndedReports, long KnownProgressSamples, long UnknownProgressSamples, long ProgressBasisPointsSum)
{
    public int? MeanProgressBasisPoints => KnownProgressSamples == 0 ? null : (int)(ProgressBasisPointsSum / KnownProgressSamples);
}
public sealed record ConsumptionGroup(string Key, ConsumptionMetrics Metrics);
public sealed record ConsumptionDay(DateOnly DayUtc, ConsumptionMetrics Metrics);
public sealed record ConsumptionReportView(DateTimeOffset GeneratedUtc, DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset AvailableFromUtc, DateTimeOffset DetailAvailableFromUtc, int RetentionDays, bool RecordingEnabled, ConsumptionMetrics Summary, ConsumptionGroup[] ByKind, ConsumptionGroup[] ByCategory, ConsumptionDay[] Daily)
{
    public string Scope => "recorded_consumption_general";
}
public sealed record ConsumptionContentMetrics(Guid ContentId, string ContentVersion, string? Slug, string? Title, string? Status, string CategoryAtStart, string SourceKind, ConsumptionMetrics Metrics);
public sealed record ConsumptionContentPage(DateTimeOffset GeneratedUtc, DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset AvailableFromUtc, DateTimeOffset DetailAvailableFromUtc, int RetentionDays, bool RecordingEnabled, ConsumptionContentMetrics[] Items, long Total, int Page, int PageSize)
{
    public string Scope => "recorded_consumption_general";
}
public sealed record AccountConsumptionReportView(DateTimeOffset GeneratedUtc, DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset AvailableFromUtc, int RetentionDays, bool RecordingEnabled, ConsumptionMetrics Summary, ConsumptionDay[] Daily, ConsumptionContentMetrics[] Items, long Total, int Page, int PageSize)
{
    public string Scope => "recorded_consumption_account";
}
public sealed record ConsumptionPruneResult(int PulsesDeleted, int SessionsDeleted, int AccountRowsDeleted, int GeneralRowsDeleted);
public interface IConsumptionRecordingService
{
    ConsumptionCapabilitiesView GetCapabilities();
    Task<CatalogResult<ConsumptionSessionView>> StartAsync(Guid accountId, string authenticationBindingHash, string slug, StartConsumptionRequest request, CancellationToken token);
    Task<CatalogResult<ConsumptionPulseReceipt>> PulseAsync(Guid accountId, string authenticationBindingHash, Guid sessionId, ConsumptionPulseRequest request, CancellationToken token);
}
public interface IConsumptionActivityReportService
{
    Task<ConsumptionReportView> GetAsync(ConsumptionReportInterval interval, CancellationToken token);
    Task<ConsumptionContentPage> ListContentAsync(ConsumptionReportInterval interval, int page, int pageSize, CancellationToken token);
    Task<AccountConsumptionReportView> GetAccountAsync(Guid accountId, ConsumptionReportInterval interval, int page, int pageSize, CancellationToken token);
}
public interface IConsumptionRetentionService
{
    Task<ConsumptionPruneResult> PruneAsync(CancellationToken token);
}
