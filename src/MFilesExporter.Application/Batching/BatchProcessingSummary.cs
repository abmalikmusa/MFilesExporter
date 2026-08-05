namespace MFilesExporter.Application.Batching;

/// <summary>Run-wide aggregate returned by the batch coordinator.</summary>
public sealed record BatchProcessingSummary
{
    public required long TotalBatches { get; init; }
    public required long TotalItems { get; init; }
    public required long TotalSucceeded { get; init; }
    public required long TotalFailed { get; init; }
    public required long TotalSkipped { get; init; }
    public required long TotalBytesWritten { get; init; }
    public required TimeSpan Elapsed { get; init; }

    /// <summary>True when the run stopped because the source produced an empty batch.</summary>
    public required bool ExhaustedSource { get; init; }

    /// <summary>True when the coordinator aborted due to the failure-rate threshold.</summary>
    public required bool AbortedOnThreshold { get; init; }

    /// <summary>Items processed per second across the whole run.</summary>
    public double ItemsPerSecond =>
        Elapsed.TotalSeconds > 0 ? TotalItems / Elapsed.TotalSeconds : 0;
}
