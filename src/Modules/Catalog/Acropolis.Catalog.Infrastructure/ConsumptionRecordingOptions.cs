namespace Acropolis.Catalog.Infrastructure;

public sealed class ConsumptionRecordingOptions
{
    public bool RecordingEnabled { get; set; }
    // Explicit opt-in for retention tests; production ignores this test-only switch.
    public bool EnableRetentionInTesting { get; set; }
}
