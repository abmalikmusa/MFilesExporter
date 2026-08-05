namespace MFilesExporter.Domain.Progress;

/// <summary>
/// Two-dimensional throughput sample: documents/second and MiB/second.
/// Value-type shape so it can be embedded in other records without
/// heap allocation.
/// </summary>
public readonly record struct ThroughputMetrics(
    double DocumentsPerSecond,
    double MebibytesPerSecond)
{
    public static ThroughputMetrics Zero { get; } = new(0, 0);

    /// <summary>
    /// Derives a throughput sample from raw counters and elapsed time.
    /// Guards against divide-by-zero when <paramref name="elapsed"/> is zero.
    /// </summary>
    public static ThroughputMetrics From(long documents, long bytes, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds <= 0) return Zero;
        return new ThroughputMetrics(
            DocumentsPerSecond: documents / elapsed.TotalSeconds,
            MebibytesPerSecond: bytes / elapsed.TotalSeconds / (1024d * 1024d));
    }
}
