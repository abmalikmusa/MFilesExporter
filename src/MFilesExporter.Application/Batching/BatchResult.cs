namespace MFilesExporter.Application.Batching;

/// <summary>Aggregate result for one processed batch.</summary>
public sealed record BatchResult
{
    public required long BatchNumber { get; init; }
    public required int Size { get; init; }
    public required int SucceededCount { get; init; }
    public required int FailedCount { get; init; }
    public required int SkippedCount { get; init; }
    public required long TotalBytesWritten { get; init; }
    public required TimeSpan Elapsed { get; init; }

    /// <summary>True when every item reached a terminal state (Success/Fail/Skip).</summary>
    public bool IsComplete => SucceededCount + FailedCount + SkippedCount == Size;

    /// <summary>Ratio of failed items to total. Range 0.0 – 1.0.</summary>
    public double FailureRate => Size == 0 ? 0.0 : (double)FailedCount / Size;

    /// <summary>Items processed per second across this batch.</summary>
    public double ItemsPerSecond =>
        Elapsed.TotalSeconds > 0 ? Size / Elapsed.TotalSeconds : 0;
}
