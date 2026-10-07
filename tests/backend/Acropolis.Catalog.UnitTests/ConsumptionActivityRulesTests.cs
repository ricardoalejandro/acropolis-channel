using Acropolis.Catalog.Application;

namespace Acropolis.Catalog.UnitTests;

public sealed class ConsumptionActivityRulesTests
{
    private static readonly ConsumptionProgressState Empty = new([], null, false, false, false);
    [Theory]
    [InlineData("2026-10-07")]
    [InlineData("2024-02-29")]
    [InlineData("0001-01-01")]
    public void AcceptsExactGregorianAsciiDates(string value) => Assert.True(ConsumptionActivityRules.TryDate(value, out _));
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-10-07\n")]
    [InlineData("2026-10-07\r\n")]
    [InlineData(" 2026-10-07")]
    [InlineData("2026-1-07")]
    [InlineData("2026-02-29")]
    [InlineData("٢٠٢٦-١٠-٠٧")]
    [InlineData("2026-10-07T00:00:00Z")]
    [InlineData("2026-10-07+05:00")]
    public void RejectsMissingMalformedAndNonUtcDateForms(string? value) => Assert.False(ConsumptionActivityRules.TryDate(value, out _));
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(365, true)]
    [InlineData(366, true)]
    [InlineData(367, false)]
    public void BoundsExplicitExclusiveIntervals(int days, bool valid)
    {
        var from = new DateOnly(2024, 1, 1);
        Assert.Equal(valid, ConsumptionActivityRules.ValidInterval(new(from, from.AddDays(days))));
    }
    [Fact]
    public void RecordingRequestAndBindingRejectEmptyIdsAndNonCanonicalVersions()
    {
        Assert.True(ConsumptionActivityRules.Valid(new(Guid.NewGuid(), new string('a', 32))));
        Assert.False(ConsumptionActivityRules.Valid(new(Guid.Empty, new string('a', 32))));
        Assert.False(ConsumptionActivityRules.Valid(new(Guid.NewGuid(), new string('A', 32))));
        Assert.False(ConsumptionActivityRules.ValidVersion(new string('a', 32) + "\n"));
        Assert.True(ConsumptionActivityRules.ValidBinding(new string('b', 64)));
        Assert.False(ConsumptionActivityRules.ValidBinding(new string('b', 64) + "\n"));
        Assert.False(ConsumptionActivityRules.ValidBinding("private@example.test"));
    }
    [Theory]
    [InlineData(0, 1000, 500)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 15001, 0)]
    [InlineData(1, 1000, -1)]
    [InlineData(1, 1000, 1001)]
    public void RejectsInvalidSequencesOrIntervals(int sequence, int interval, int active) =>
        Assert.False(ConsumptionActivityRules.Valid(new(sequence, interval, active, Reading: new(5000, [new(0, 5000)])), "reading"));
    [Fact]
    public void RequiresExactlyOneObservationMatchingTheAuthorizedSource()
    {
        var reading = new ReadingConsumptionObservation(5000, [new(0, 5000)]);
        var media = new MediaConsumptionObservation("playing", 1000, null, 1000, [new(0, 1000)]);
        Assert.False(ConsumptionActivityRules.Valid(new(1, 1000, 1000), "reading"));
        Assert.False(ConsumptionActivityRules.Valid(new(1, 1000, 1000, media, reading), "youtube"));
        Assert.False(ConsumptionActivityRules.Valid(new(1, 1000, 1000, Media: media), "reading"));
        Assert.False(ConsumptionActivityRules.Valid(new(1, 1000, 1000, Reading: reading), "youtube"));
        Assert.False(ConsumptionActivityRules.Valid(new(1, 1000, 1000, Media: media), "audio-aws"));
    }
    [Theory]
    [InlineData(-1, 500)]
    [InlineData(500, 500)]
    [InlineData(501, 500)]
    [InlineData(0, 10001)]
    public void RejectsInvalidReadingCoverage(int from, int to) => Assert.False(ConsumptionActivityRules.ValidRanges([new(from, to)], 10000));
    [Fact]
    public void RejectsUnboundedUnsortedAndOverlappingRanges()
    {
        Assert.False(ConsumptionActivityRules.ValidRanges(null, 10000));
        Assert.False(ConsumptionActivityRules.ValidRanges(Enumerable.Range(0, 17).Select(x => new ConsumptionRange(x * 2, x * 2 + 1)).ToArray(), 10000));
        Assert.False(ConsumptionActivityRules.ValidRanges([new(200, 300), new(0, 100)], 10000));
        Assert.False(ConsumptionActivityRules.ValidRanges([new(0, 1000), new(500, 1500)], 10000));
        Assert.True(ConsumptionActivityRules.ValidRanges([new(0, 500), new(500, 1000)], 10000));
    }
    [Fact]
    public void MetadataNotLoadedIsUnknownAndZeroMustBeNormalizedByClient()
    {
        Assert.True(ConsumptionActivityRules.Valid(Media(duration: null), "youtube"));
        Assert.False(ConsumptionActivityRules.Valid(Media(duration: 0), "youtube"));
        Assert.Null(ConsumptionActivityProgress.Observe(Empty, Media(duration: null), "youtube", 1000).ProgressBasisPoints);
        var known = ConsumptionActivityProgress.Observe(Empty, Media(duration: 10000), "youtube", 1000);
        Assert.Equal(1000, known.ProgressBasisPoints);
        var changed = ConsumptionActivityProgress.Observe(known.State, Media(duration: 13000), "youtube", 1000);
        Assert.True(changed.State.DurationChanged); Assert.Null(changed.ProgressBasisPoints);
        Assert.Null(ConsumptionActivityProgress.Observe(changed.State, Media(duration: 10000), "youtube", 1000).ProgressBasisPoints);
    }
    [Theory]
    [InlineData(1000, 1000, 1000)]
    [InlineData(1000, 400, 400)]
    [InlineData(1000, -1, 0)]
    [InlineData(15000, 20000, 15000)]
    public void CreditCannotExceedServerElapsedOrClientActiveInterval(int active, int elapsed, int expected)
    {
        var request = Media(active: active, covered: Math.Max(1, Math.Min(active, Math.Max(0, elapsed))), duration: 86400000);
        if (elapsed < 0) Assert.Throws<ArgumentException>(() => ConsumptionActivityProgress.Observe(Empty, request, "youtube", elapsed));
        else Assert.Equal(expected, ConsumptionActivityProgress.Observe(Empty, request, "youtube", elapsed).CreditedMs);
    }
    [Fact]
    public void SmallReportedSegmentCannotCreditTheWholeHeartbeatOrSpeedAllowance()
    {
        Assert.Equal(10, ConsumptionActivityProgress.Observe(Empty, Media(active: 1000, covered: 10), "youtube", 1000).CreditedMs);
        Assert.Throws<ArgumentException>(() => ConsumptionActivityProgress.Observe(Empty, Media(active: 1000, covered: 1001), "youtube", 1000));
        var rate = Media(active: 1000, covered: 2000) with { Media = new("playing", 2000, 10000, 2000, [new(0, 2000)]) };
        Assert.Equal(1000, ConsumptionActivityProgress.Observe(Empty, rate, "youtube", 1000).CreditedMs);
        Assert.Equal(0, ConsumptionActivityProgress.Observe(Empty, Media() with { Media = new("buffering", 1000, 10000, 1000, []) }, "youtube", 1000).CreditedMs);
    }
    [Fact]
    public void SeekAndRewatchPreserveCoverageWithoutFillingGapsOrForcingCompletion()
    {
        var first = ConsumptionActivityProgress.Observe(Empty, Media(), "youtube", 1000);
        var seek = Media() with { Media = new("ended", 10000, 10000, 1000, [new(9000, 10000)]) };
        var last = ConsumptionActivityProgress.Observe(first.State, seek, "youtube", 1000);
        Assert.Equal(2000, last.ProgressBasisPoints); Assert.True(last.EndedNow); Assert.True(last.State.EndedReported);
        Assert.Equal([new ConsumptionRange(0, 1000), new(9000, 10000)], last.State.Coverage);
        var rewatch = ConsumptionActivityProgress.Observe(last.State, Media(), "youtube", 1000);
        Assert.Equal(2000, rewatch.ProgressBasisPoints); Assert.Equal(1000, rewatch.CreditedMs); Assert.False(rewatch.EndedNow);
    }
    [Fact]
    public void CoverageStorageIsBoundedAndTruncationMarksProgressUnknown()
    {
        var previous = Empty with { Coverage = Enumerable.Range(0, 512).Select(x => new ConsumptionRange(x * 2, x * 2 + 1)).ToArray(), DurationMs = 10000 };
        var request = Media() with { Media = new("playing", 2001, 10000, 1000, [new(2000, 2001)]) };
        var result = ConsumptionActivityProgress.Observe(previous, request, "youtube", 1000);
        Assert.Equal(512, result.State.Coverage.Length); Assert.True(result.State.CoverageIncomplete); Assert.Null(result.ProgressBasisPoints);
    }
    [Fact]
    public void ReadingCoverageAndTimeAreSignalsAndNeverProduceVideoEndEvent()
    {
        var reading = new ConsumptionPulseRequest(1, 1000, 500, Reading: new(10000, [new(0, 5000), new(5000, 10000)]));
        var result = ConsumptionActivityProgress.Observe(Empty, reading, "reading", 1000);
        Assert.Equal(500, result.CreditedMs); Assert.Equal(10000, result.ProgressBasisPoints); Assert.False(result.EndedNow); Assert.False(result.State.EndedReported);
        Assert.Equal(0, ConsumptionActivityProgress.Observe(Empty, reading with { ActiveMs = 0 }, "reading", 1000).CreditedMs);
    }
    [Fact]
    public void GeneralWindowUses365UtcDatesAndDetailUsesNinetyUtcDates()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.FromHours(5));
        var general = ConsumptionActivityRules.GeneralAvailableFrom(now);
        Assert.Equal(TimeSpan.Zero, general.Offset); Assert.Equal(0, general.Hour);
        Assert.Equal(364, DateOnly.FromDateTime(now.UtcDateTime).DayNumber - DateOnly.FromDateTime(general.UtcDateTime).DayNumber);
        Assert.Equal(90, ConsumptionActivityRules.DetailRetentionDays);
        Assert.Equal(89, DateOnly.FromDateTime(now.UtcDateTime).DayNumber - DateOnly.FromDateTime(ConsumptionActivityRules.DetailAvailableFrom(now).UtcDateTime).DayNumber);
        Assert.Equal(0, ConsumptionActivityRules.DetailAvailableFrom(now).Hour);
        Assert.True(new DateTimeOffset(DateOnly.FromDateTime(now.UtcDateTime).AddDays(-89).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) > now.ToUniversalTime().AddDays(-90));
    }
    [Fact]
    public void HashReplaysAndProgressMeanRetainCorrectDenominators()
    {
        var request = Media();
        Assert.Equal(ConsumptionActivityRules.CanonicalHash(request), ConsumptionActivityRules.CanonicalHash(request with { }));
        Assert.NotEqual(ConsumptionActivityRules.CanonicalHash(request), ConsumptionActivityRules.CanonicalHash(request with { Sequence = 2 }));
        var metrics = new ConsumptionMetrics(null, 1, 3, 1000, 1, 3, 0, 17000);
        Assert.Equal(5666, metrics.MeanProgressBasisPoints);
        Assert.Null(new ConsumptionMetrics(null, 0, 1, 0, 0, 0, 1, 0).MeanProgressBasisPoints);
        Assert.Null(metrics.AccountsWithActivity);
    }
    private static ConsumptionPulseRequest Media(int active = 1000, int covered = 1000, int? duration = 10000) =>
        new(1, active, active, new("playing", covered, duration, 1000, [new(0, covered)]));
    [Fact]
    public void MissingCurrentMetadataDoesNotReuseAnOldKnownDurationAsValidProgress()
    {
        var previous = new ConsumptionProgressState([new(0, 1000)], 10000, false, false, false);
        var observation = new ConsumptionPulseRequest(2, 1000, 1000, Media: new("playing", 2000, null, 1000, [new(1000, 2000)]));
        var result = ConsumptionActivityProgress.Observe(previous, observation, "youtube", 1000);
        Assert.Null(result.ProgressBasisPoints); Assert.Equal(10000, result.State.DurationMs); Assert.Equal(1000, result.CreditedMs);
    }

}
