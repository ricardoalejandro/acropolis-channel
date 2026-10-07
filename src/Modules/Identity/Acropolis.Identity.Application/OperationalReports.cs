namespace Acropolis.Identity.Application;

public sealed record IdentityReportCount(string Key, long Count);
public sealed record IdentityReportView(DateTimeOffset GeneratedUtc, long Total,
    IdentityReportCount[] ByStatus, IdentityReportCount[] ByLevel)
{
    public string Scope => "current";
}
public interface IIdentityReportService
{
    Task<IdentityReportView> GetCurrentAsync(CancellationToken token);
}
