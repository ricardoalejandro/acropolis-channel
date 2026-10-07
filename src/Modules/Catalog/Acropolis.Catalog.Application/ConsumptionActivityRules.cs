using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Acropolis.Catalog.Application;

public static class ConsumptionActivityRules
{
    public const int DetailRetentionDays = 90;
    public const int GeneralRetentionDays = 365;
    public const int MaximumPulseMs = 15000;
    public const int MaximumRanges = 16;
    public const int MaximumCoverageRanges = 512;
    public static readonly string[] SourceKinds = ["reading", "youtube"];
    public static bool ValidBinding(string? value) => value is not null && Regex.IsMatch(value, @"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant);
    public static bool ValidVersion(string? value) => value is not null && Regex.IsMatch(value, @"\A[a-f0-9]{32}\z", RegexOptions.CultureInvariant);
    public static bool Valid(StartConsumptionRequest? request) => request is not null && request.VisitId != Guid.Empty && ValidVersion(request.ContentVersion);
    public static bool Valid(ConsumptionPulseRequest? request, string kind)
    {
        if (request is null || request.Sequence is < 1 or > 1000000 || request.IntervalMs is < 1 or > MaximumPulseMs || request.ActiveMs < 0 || request.ActiveMs > request.IntervalMs) return false;
        if (kind == "reading")
            return request.Media is null && request.Reading is { PositionBasisPoints: >= 0 and <= 10000 } reading && ValidRanges(reading.ExposedRanges, 10000);
        if (kind != "youtube" || request.Reading is not null || request.Media is not { } media) return false;
        return (media.State is "playing" or "paused" or "buffering" or "ended") && media.PositionMs is >= 0 and <= 86400000 &&
            (media.DurationMs is null or > 0 and <= 86400000) && media.PlaybackRateMilli is >= 250 and <= 4000 &&
            (media.DurationMs is null || media.PositionMs <= media.DurationMs + 1000) && ValidRanges(media.Segments, media.DurationMs ?? 86400000) &&
            (media.State != "buffering" || media.Segments.Length == 0);
    }
    public static bool ValidRanges(ConsumptionRange[]? ranges, int maximum)
    {
        if (ranges is null || ranges.Length > MaximumRanges) return false;
        var last = -1;
        foreach (var range in ranges)
        {
            if (range is null || range.From < 0 || range.To <= range.From || range.To > maximum || range.From < last) return false;
            last = range.To;
        }
        return true;
    }
    public static bool TryDate(string? value, out DateOnly date)
    {
        date = default;
        return value is not null && Regex.IsMatch(value, @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z", RegexOptions.CultureInvariant) &&
            DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
    public static bool ValidInterval(ConsumptionReportInterval? interval) => interval is not null && interval.To.DayNumber - interval.From.DayNumber is >= 1 and <= 366;
    public static bool ValidPage(int page, int pageSize) => page is >= 1 and <= 1000000 && pageSize is >= 1 and <= 100;
    public static DateTimeOffset DetailAvailableFrom(DateTimeOffset now) => new(DateOnly.FromDateTime(now.UtcDateTime).AddDays(1 - DetailRetentionDays).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    public static DateTimeOffset GeneralAvailableFrom(DateTimeOffset now) => new(DateOnly.FromDateTime(now.UtcDateTime).AddDays(1 - GeneralRetentionDays).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    public static DateTimeOffset ServerUtc(DateTimeOffset now) => DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
    public static string CanonicalHash(ConsumptionPulseRequest request) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
}
