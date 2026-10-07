namespace Acropolis.Catalog.Application;

public sealed record ConsumptionProgressState(ConsumptionRange[] Coverage, int? DurationMs, bool DurationChanged, bool CoverageIncomplete, bool EndedReported);
public sealed record ConsumptionProgressUpdate(ConsumptionProgressState State, int CreditedMs, int? ProgressBasisPoints, bool EndedNow);
public static class ConsumptionActivityProgress
{
    public static ConsumptionProgressUpdate Observe(ConsumptionProgressState previous, ConsumptionPulseRequest request, string kind, int elapsedServerMs)
    {
        if (!ConsumptionActivityRules.Valid(request, kind)) throw new ArgumentException("Invalid consumption observation.", nameof(request));
        var credit = Math.Min(request.ActiveMs, Math.Clamp(elapsedServerMs, 0, ConsumptionActivityRules.MaximumPulseMs));
        var ranges = kind == "reading" ? request.Reading!.ExposedRanges : request.Media!.Segments;
        var duration = previous.DurationMs;
        var changed = previous.DurationChanged;
        var ended = previous.EndedReported;
        if (kind == "youtube")
        {
            var media = request.Media!;
            var covered = ranges.Sum(x => (long)x.To - x.From);
            if (covered > (long)credit * media.PlaybackRateMilli / 1000) throw new ArgumentException("Observation exceeds server interval.", nameof(request));
            credit = (int)Math.Min(credit, covered * 1000 / media.PlaybackRateMilli);
            if (media.DurationMs is { } reported)
            {
                if (duration is { } known && Math.Abs((long)known - reported) > 1000) changed = true;
                duration ??= reported;
            }
            ended |= media.State == "ended";
        }
        var merged = Merge(previous.Coverage, ranges);
        var incomplete = previous.CoverageIncomplete || merged.Length > ConsumptionActivityRules.MaximumCoverageRanges;
        if (merged.Length > ConsumptionActivityRules.MaximumCoverageRanges) merged = merged.Take(ConsumptionActivityRules.MaximumCoverageRanges).ToArray();
        var denominator = kind == "reading" ? 10000 : request.Media!.DurationMs is null ? null : duration;
        int? progress = incomplete || changed || denominator is null ? null : (int)Math.Min(10000L, merged.Sum(x => (long)x.To - x.From) * 10000 / denominator.Value);
        return new(new(merged, duration, changed, incomplete, ended), credit, progress, ended && !previous.EndedReported);
    }
    public static ConsumptionRange[] Merge(ConsumptionRange[] previous, ConsumptionRange[] added)
    {
        var result = new List<ConsumptionRange>();
        foreach (var range in previous.Concat(added).OrderBy(x => x.From).ThenBy(x => x.To))
        {
            if (result.Count == 0 || range.From > result[^1].To) result.Add(range);
            else result[^1] = new(result[^1].From, Math.Max(result[^1].To, range.To));
        }
        return result.ToArray();
    }
}
