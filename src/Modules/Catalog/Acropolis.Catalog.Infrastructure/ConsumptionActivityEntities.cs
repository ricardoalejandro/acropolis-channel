namespace Acropolis.Catalog.Infrastructure;

public sealed class ConsumptionSession
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string AuthenticationBindingHash { get; set; } = "";
    public Guid VisitId { get; set; }
    public Guid ContentId { get; set; }
    public string ContentVersion { get; set; } = "";
    public string CategoryAtStart { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset LastReceivedUtc { get; set; }
    public int LastSequence { get; set; }
    public string CoverageJson { get; set; } = "[]";
    public int? DurationMs { get; set; }
    public bool DurationChanged { get; set; }
    public bool CoverageIncomplete { get; set; }
    public bool EndedReported { get; set; }
}
public sealed class ConsumptionPulse
{
    public Guid SessionId { get; set; }
    public int Sequence { get; set; }
    public string CanonicalHash { get; set; } = "";
    public DateTimeOffset ReceivedUtc { get; set; }
    public int CreditedMs { get; set; }
    public int? ProgressBasisPoints { get; set; }
    public bool CoverageIncomplete { get; set; }
    public bool EndedReported { get; set; }
    public bool EndedNow { get; set; }
}
// This table has no AccountId, session identifier, binding hash, email, IP or referrer.
public sealed class ConsumptionDaily
{
    public DateOnly DayUtc { get; set; }
    public Guid ContentId { get; set; }
    public string ContentVersion { get; set; } = "";
    public string CategoryAtStart { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public long Starts { get; set; }
    public long RecordedPulses { get; set; }
    public long CreditedMs { get; set; }
    public long EndedReports { get; set; }
    public long KnownProgressSamples { get; set; }
    public long UnknownProgressSamples { get; set; }
    public long ProgressBasisPointsSum { get; set; }
}

public sealed class ConsumptionAccountDaily
{
    public Guid AccountId { get; set; }
    public DateOnly DayUtc { get; set; }
    public Guid ContentId { get; set; }
    public string ContentVersion { get; set; } = "";
    public string CategoryAtStart { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public long Starts { get; set; }
    public long RecordedPulses { get; set; }
    public long CreditedMs { get; set; }
    public long EndedReports { get; set; }
    public long KnownProgressSamples { get; set; }
    public long UnknownProgressSamples { get; set; }
    public long ProgressBasisPointsSum { get; set; }
}
